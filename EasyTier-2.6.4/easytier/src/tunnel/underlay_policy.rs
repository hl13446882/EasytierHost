//! Explicit opt-in binding for TCP/UDP overlay-maintenance sockets only.
//! A Host process manages one network and must restart Core on physical-network changes.
use super::common::{self, BindDev};
use std::{
    io,
    net::{IpAddr, Ipv4Addr, SocketAddr},
    sync::OnceLock,
};
use tokio::net::{TcpStream, ToSocketAddrs, UdpSocket};

#[derive(Debug, Clone)]
pub struct UnderlayBindingPolicy {
    pub source_ipv4: Ipv4Addr,
    pub bind_device: String,
}

static POLICY: OnceLock<UnderlayBindingPolicy> = OnceLock::new();

impl UnderlayBindingPolicy {
    pub fn resolve(&self, requested: SocketAddr) -> io::Result<(SocketAddr, BindDev)> {
        // Local RPC and IPv6 retain their own routing semantics. Host only manages IPv4 defaults.
        if requested.ip().is_loopback() || requested.is_ipv6() {
            return Ok((requested, BindDev::Auto));
        }
        if !requested.ip().is_unspecified() && requested.ip() != IpAddr::V4(self.source_ipv4) {
            return Err(io::Error::new(
                io::ErrorKind::AddrNotAvailable,
                "underlay socket requested a different source interface",
            ));
        }
        Ok((
            SocketAddr::new(self.source_ipv4.into(), requested.port()),
            BindDev::Custom(self.bind_device.clone()),
        ))
    }
}

pub fn initialize(source_ipv4: Ipv4Addr) -> anyhow::Result<()> {
    anyhow::ensure!(
        cfg!(any(target_os = "windows", target_os = "linux")),
        "underlay binding is supported on Windows/Linux only"
    );
    anyhow::ensure!(
        !source_ipv4.is_unspecified()
            && !source_ipv4.is_loopback()
            && !source_ipv4.is_multicast()
            && source_ipv4 != Ipv4Addr::BROADCAST,
        "invalid underlay IPv4 source"
    );
    let bind_device = common::get_interface_name_by_ip(&source_ipv4.into())
        .ok_or_else(|| anyhow::anyhow!("underlay source is not assigned to a local interface"))?;
    POLICY
        .set(UnderlayBindingPolicy {
            source_ipv4,
            bind_device,
        })
        .map_err(|_| {
            anyhow::anyhow!("underlay binding policy is immutable; restart Core to rebind")
        })
}

pub fn is_enabled() -> bool {
    POLICY.get().is_some()
}

pub fn resolve(requested: SocketAddr, original: BindDev) -> io::Result<(SocketAddr, BindDev)> {
    match POLICY.get() {
        Some(policy) if requested.is_ipv4() && !requested.ip().is_loopback() => {
            policy.resolve(requested)
        }
        _ => Ok((requested, original)),
    }
}

pub async fn bind_udp<A: ToSocketAddrs>(address: A) -> io::Result<UdpSocket> {
    if POLICY.get().is_none() {
        return UdpSocket::bind(address).await;
    }
    let mut error = io::Error::new(
        io::ErrorKind::AddrNotAvailable,
        "no suitable underlay address",
    );
    for addr in tokio::net::lookup_host(address).await? {
        match common::bind::<UdpSocket>().addr(addr).underlay(true).call() {
            Ok(socket) => return Ok(socket),
            Err(e) => error = io::Error::other(e),
        }
    }
    Err(error)
}

pub async fn connect_tcp(destination: SocketAddr) -> io::Result<TcpStream> {
    if POLICY.get().is_none() || destination.ip().is_loopback() {
        return TcpStream::connect(destination).await;
    }
    let address = if destination.is_ipv4() {
        "0.0.0.0:0"
    } else {
        "[::]:0"
    }
    .parse()
    .unwrap();
    common::bind::<tokio::net::TcpSocket>()
        .addr(address)
        .underlay(true)
        .call()
        .map_err(io::Error::other)?
        .connect(destination)
        .await
}

#[cfg(test)]
mod tests {
    use super::*;
    fn policy() -> UnderlayBindingPolicy {
        UnderlayBindingPolicy {
            source_ipv4: "192.0.2.5".parse().unwrap(),
            bind_device: "wan0".into(),
        }
    }
    #[test]
    fn unspecified_underlay_uses_physical_source_and_device() {
        let (address, device) = policy().resolve("0.0.0.0:11010".parse().unwrap()).unwrap();
        assert_eq!(address.to_string(), "192.0.2.5:11010");
        assert!(matches!(device, BindDev::Custom(ref name) if name == "wan0"));
    }
    #[test]
    fn rejects_overlay_or_stale_physical_source() {
        assert!(policy().resolve("10.10.0.11:0".parse().unwrap()).is_err());
        assert!(policy().resolve("192.0.2.6:0".parse().unwrap()).is_err());
    }
    #[test]
    fn loopback_and_ipv6_remain_unchanged() {
        for addr in ["127.0.0.1:15888", "[::1]:15888", "[::]:0"] {
            let requested = addr.parse().unwrap();
            assert_eq!(policy().resolve(requested).unwrap().0, requested);
        }
    }
    #[tokio::test]
    async fn unconfigured_policy_keeps_udp_binding_compatible() {
        let socket = bind_udp("127.0.0.1:0").await.unwrap();
        assert!(socket.local_addr().unwrap().ip().is_loopback());
    }
}
