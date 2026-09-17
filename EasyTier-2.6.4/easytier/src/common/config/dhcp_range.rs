//! Optional deterministic DHCP pool. Explicit subnet also works with a no-TUN seed.
use cidr::Ipv4Inet;
use serde::{Deserialize, Serialize};
use std::{collections::HashSet, net::Ipv4Addr};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DhcpRange {
    pub network: Ipv4Inet,
    pub start: Ipv4Addr,
    pub end: Ipv4Addr,
}

impl DhcpRange {
    pub fn validate(&self) -> anyhow::Result<()> {
        anyhow::ensure!(self.start <= self.end, "DHCP start exceeds end");
        for ip in [self.start, self.end] {
            anyhow::ensure!(
                self.network.network().contains(&ip),
                "DHCP address outside subnet"
            );
            anyhow::ensure!(
                ip != self.network.first_address() && ip != self.network.last_address(),
                "DHCP pool includes network or broadcast"
            );
        }
        Ok(())
    }

    pub fn contains(&self, ip: Ipv4Addr) -> bool {
        ip >= self.start && ip <= self.end
    }

    pub fn candidate(&self, used: &HashSet<Ipv4Inet>) -> Option<Ipv4Inet> {
        let used: HashSet<Ipv4Addr> = used.iter().map(|ip| ip.address()).collect();
        (u32::from(self.start)..=u32::from(self.end))
            .map(Ipv4Addr::from)
            .find(|ip| !used.contains(ip))
            .map(|ip| Ipv4Inet::new(ip, self.network.network_length()).unwrap())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn pool() -> DhcpRange {
        DhcpRange {
            network: "10.10.0.0/16".parse().unwrap(),
            start: "10.10.0.11".parse().unwrap(),
            end: "10.10.255.254".parse().unwrap(),
        }
    }
    #[test]
    fn selects_lowest_hole_and_skips_reserved() {
        let p = pool();
        p.validate().unwrap();
        let mut used = HashSet::new();
        assert_eq!(p.candidate(&used).unwrap().address(), p.start);
        used.insert("10.10.0.11/24".parse().unwrap());
        used.insert("10.10.0.13/16".parse().unwrap());
        assert_eq!(
            p.candidate(&used).unwrap().address().to_string(),
            "10.10.0.12"
        );
        assert!(!p.contains("10.10.0.10".parse().unwrap()));
        assert!(!p.contains("10.10.255.255".parse().unwrap()));
    }
    #[test]
    fn rejects_invalid_pools() {
        for (start, end) in [
            ("10.10.0.0", "10.10.0.11"),
            ("10.10.0.11", "10.10.255.255"),
            ("10.10.0.12", "10.10.0.11"),
            ("10.9.0.11", "10.10.0.11"),
        ] {
            let mut p = pool();
            p.start = start.parse().unwrap();
            p.end = end.parse().unwrap();
            assert!(p.validate().is_err());
        }
    }
    #[test]
    fn exhaustion_and_recovery() {
        let mut p = pool();
        p.end = p.start;
        let mut used = HashSet::from([p.candidate(&HashSet::new()).unwrap()]);
        assert!(p.candidate(&used).is_none());
        used.clear();
        assert!(p.candidate(&used).is_some());
    }

    #[test]
    fn config_round_trip_and_legacy_default() {
        use super::super::{ConfigLoader, TomlConfigLoader};
        let legacy = TomlConfigLoader::new_from_str("dhcp = true").unwrap();
        assert!(legacy.get_dhcp_range().is_none());
        let config = TomlConfigLoader::new_from_str("dhcp = true\n[dhcp_range]\nnetwork = '10.10.0.0/16'\nstart = '10.10.0.11'\nend = '10.10.255.254'\n").unwrap();
        assert_eq!(config.get_dhcp_range(), Some(pool()));
        let restored = TomlConfigLoader::new_from_str(&config.dump()).unwrap();
        assert_eq!(restored.get_dhcp_range(), Some(pool()));
    }
}
