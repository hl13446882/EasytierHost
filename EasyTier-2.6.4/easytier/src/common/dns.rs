use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;

use anyhow::Context;
use hickory_proto::runtime::{RuntimeProvider, TokioRuntimeProvider};
use hickory_proto::xfer::Protocol;
use hickory_resolver::Resolver;
use hickory_resolver::config::{LookupIpStrategy, NameServerConfig, ResolverConfig, ResolverOpts};
use hickory_resolver::name_server::GenericConnector;
use hickory_resolver::system_conf::read_system_conf;
use once_cell::sync::Lazy;
use tokio::net::lookup_host;

use super::error::Error;

/// DNS used by tunnel discovery must follow the same binding policy as its tunnels.
#[derive(Clone, Default)]
pub struct UnderlayDnsRuntime(TokioRuntimeProvider);

impl RuntimeProvider for UnderlayDnsRuntime {
    type Handle = <TokioRuntimeProvider as RuntimeProvider>::Handle;
    type Timer = <TokioRuntimeProvider as RuntimeProvider>::Timer;
    type Udp = <TokioRuntimeProvider as RuntimeProvider>::Udp;
    type Tcp = <TokioRuntimeProvider as RuntimeProvider>::Tcp;

    fn create_handle(&self) -> Self::Handle {
        self.0.create_handle()
    }

    fn connect_tcp(
        &self,
        server: SocketAddr,
        bind: Option<SocketAddr>,
        timeout: Option<std::time::Duration>,
    ) -> std::pin::Pin<Box<dyn Send + std::future::Future<Output = std::io::Result<Self::Tcp>>>>
    {
        if !crate::tunnel::underlay_policy::is_enabled() {
            return self.0.connect_tcp(server, bind, timeout);
        }
        Box::pin(async move {
            let local = bind.unwrap_or_else(|| {
                if server.is_ipv4() {
                    "0.0.0.0:0"
                } else {
                    "[::]:0"
                }
                .parse()
                .unwrap()
            });
            let socket = crate::tunnel::common::bind::<tokio::net::TcpSocket>()
                .addr(local)
                .underlay(true)
                .call()
                .map_err(std::io::Error::other)?;
            socket.set_nodelay(true)?;
            let stream = tokio::time::timeout(
                timeout.unwrap_or(std::time::Duration::from_secs(5)),
                socket.connect(server),
            )
            .await??;
            Ok(hickory_proto::runtime::iocompat::AsyncIoTokioAsStd(stream))
        })
    }

    fn bind_udp(
        &self,
        local: SocketAddr,
        server: SocketAddr,
    ) -> std::pin::Pin<Box<dyn Send + std::future::Future<Output = std::io::Result<Self::Udp>>>>
    {
        if !crate::tunnel::underlay_policy::is_enabled() {
            return self.0.bind_udp(local, server);
        }
        Box::pin(crate::tunnel::underlay_policy::bind_udp(local))
    }
}

pub fn get_default_resolver_config() -> ResolverConfig {
    let mut default_resolve_config = ResolverConfig::new();
    default_resolve_config.add_name_server(NameServerConfig::new(
        "223.5.5.5:53".parse().unwrap(),
        Protocol::Udp,
    ));
    default_resolve_config.add_name_server(NameServerConfig::new(
        "180.184.1.1:53".parse().unwrap(),
        Protocol::Udp,
    ));
    default_resolve_config
}

pub static ALLOW_USE_SYSTEM_DNS_RESOLVER: Lazy<AtomicBool> = Lazy::new(|| AtomicBool::new(true));

pub static RESOLVER: Lazy<Arc<Resolver<GenericConnector<UnderlayDnsRuntime>>>> = Lazy::new(|| {
    // A system stub may forward back through the TUN after default-route takeover.
    let system_cfg = if crate::tunnel::underlay_policy::is_enabled() {
        None
    } else {
        read_system_conf().ok()
    };
    let mut cfg = get_default_resolver_config();
    let mut opt = ResolverOpts::default();
    if let Some(s) = system_cfg {
        for ns in s.0.name_servers() {
            cfg.add_name_server(ns.clone());
        }
        opt = s.1;
    }
    opt.ip_strategy = LookupIpStrategy::Ipv4AndIpv6;
    let builder =
        Resolver::builder_with_config(cfg, GenericConnector::new(UnderlayDnsRuntime::default()))
            .with_options(opt);
    Arc::new(builder.build())
});

pub async fn lookup_maintenance_host(host: &str) -> Result<Vec<SocketAddr>, Error> {
    if !crate::tunnel::underlay_policy::is_enabled() {
        return Ok(lookup_host(host)
            .await
            .with_context(|| "maintenance DNS lookup failed")?
            .collect());
    }
    let url = url::Url::parse(&format!("udp://{host}"))
        .with_context(|| "invalid maintenance endpoint")?;
    socket_addrs(&url, || None).await
}

pub async fn resolve_txt_record(domain_name: &str) -> Result<String, Error> {
    let r = RESOLVER.clone();
    let response = r
        .txt_lookup(domain_name)
        .await
        .with_context(|| format!("txt_lookup failed, domain_name: {}", domain_name))?;

    let txt_record = response
        .iter()
        .next()
        .with_context(|| format!("no txt record found, domain_name: {}", domain_name))?;

    let txt_data = String::from_utf8_lossy(&txt_record.txt_data()[0]);
    tracing::info!(?txt_data, ?domain_name, "get txt record");

    Ok(txt_data.to_string())
}

pub async fn socket_addrs(
    url: &url::Url,
    default_port_number: impl Fn() -> Option<u16>,
) -> Result<Vec<SocketAddr>, Error> {
    let host = url.host().ok_or(Error::InvalidUrl(url.to_string()))?;
    let port = url
        .port()
        .or_else(default_port_number)
        .ok_or(Error::InvalidUrl(url.to_string()))?;

    // if host is an ip address, return it directly
    match host {
        url::Host::Ipv4(ip) => return Ok(vec![SocketAddr::new(std::net::IpAddr::V4(ip), port)]),
        url::Host::Ipv6(ip) => return Ok(vec![SocketAddr::new(std::net::IpAddr::V6(ip), port)]),
        _ => {}
    }
    let host = host.to_string();

    if !crate::tunnel::underlay_policy::is_enabled()
        && ALLOW_USE_SYSTEM_DNS_RESOLVER.load(std::sync::atomic::Ordering::Relaxed)
    {
        let socket_addr = format!("{}:{}", host, port);
        match lookup_host(socket_addr).await {
            Ok(a) => {
                let a = a.collect();
                tracing::debug!(?a, "system dns lookup done");
                return Ok(a);
            }
            Err(e) => {
                tracing::error!(?e, "system dns lookup failed");
            }
        }
    }

    // use hickory_resolver
    let ret = RESOLVER.lookup_ip(&host).await.with_context(|| {
        format!(
            "hickory dns lookup_ip failed, host: {}, port: {}",
            host, port
        )
    })?;
    Ok(ret
        .iter()
        .map(|ip| SocketAddr::new(ip, port))
        .collect::<Vec<_>>())
}

#[cfg(test)]
mod tests {
    use super::*;
    use guarden::defer;

    #[tokio::test]
    async fn underlay_dns_runtime_preserves_unconfigured_loopback() {
        let runtime = UnderlayDnsRuntime::default();
        let udp = runtime
            .bind_udp(
                "127.0.0.1:0".parse().unwrap(),
                "127.0.0.1:53".parse().unwrap(),
            )
            .await
            .unwrap();
        assert!(udp.local_addr().unwrap().ip().is_loopback());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let stream = runtime
            .connect_tcp(
                listener.local_addr().unwrap(),
                None,
                Some(std::time::Duration::from_secs(2)),
            )
            .await
            .unwrap();
        assert!(stream.0.local_addr().unwrap().ip().is_loopback());
    }

    #[tokio::test]
    async fn underlay_maintenance_numeric_endpoint_preserved() {
        let ips = lookup_maintenance_host("127.0.0.1:3478").await.unwrap();
        assert_eq!(ips, vec!["127.0.0.1:3478".parse::<SocketAddr>().unwrap()]);
    }

    #[tokio::test]
    async fn test_socket_addrs() {
        let url = url::Url::parse("tcp://github-ci-test.easytier.cn:80").unwrap();
        let addrs = socket_addrs(&url, || Some(80)).await.unwrap();
        assert_eq!(2, addrs.len(), "addrs: {:?}", addrs);
        println!("addrs: {:?}", addrs);

        ALLOW_USE_SYSTEM_DNS_RESOLVER.store(false, std::sync::atomic::Ordering::Relaxed);
        defer!(
            ALLOW_USE_SYSTEM_DNS_RESOLVER.store(true, std::sync::atomic::Ordering::Relaxed);
        );
        let addrs = socket_addrs(&url, || Some(80)).await.unwrap();
        assert_eq!(2, addrs.len(), "addrs: {:?}", addrs);
        println!("addrs2: {:?}", addrs);
    }

    #[tokio::test]
    async fn socket_addrs_preserves_explicit_zero_port() {
        let cases = [
            ("ws://127.0.0.1:0", 80, 0),
            ("wss://127.0.0.1:0", 443, 0),
            ("ws://127.0.0.1", 80, 80),
            ("wss://127.0.0.1", 443, 443),
        ];

        for (raw_url, default_port, expected_port) in cases {
            let url = url::Url::parse(raw_url).unwrap();
            let addrs = socket_addrs(&url, || Some(default_port)).await.unwrap();
            assert_eq!(
                addrs,
                vec![SocketAddr::from(([127, 0, 0, 1], expected_port))]
            );
        }
    }
}
