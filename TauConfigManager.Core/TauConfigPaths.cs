namespace TauConfigManager.Core;

public static class TauConfigPaths
{
    public const string ScopeVersion = "non-wireless-core-2026-07";
    private const int MaxGreTunnelProfiles = 100;

    public static IReadOnlyList<string> DefaultWhitelist { get; } =
    [
        "ltehost/config/reserve_config.cfg",
        "ltehost/config/repair.cfg",
        "ltehost/config/repair_bak.cfg",
        "ltehost/config/serial_com.cfg",
        "ltehost/config/snmp_oid.conf",
        "ltehost/config/private_oid.conf",
        "ltehost/config/ssd.cfg",
        "ltehost/config/arping_bd_req_ip.cfg",
        "ltehost/config/sim_select_detecting.cfg",
        "ltehost/config/soft_sim_modem1.cfg",
        "ltehost/config/soft_sim_modem2.cfg",
        "ltehost/config/double_soft_sim.cfg",
        "ltehost/config/tcpdump_eth0.cfg",
        "ltehost/config/tcpdump_eth1.cfg",
        "ltehost/config/tcpdump_eth2.cfg",
        "ltehost/config/vlan.conf",
        "ltehost/config/sw/sw_alarm.cfg",
        "ltehost/config/sw/sw_bandwidth.cfg",
        "ltehost/config/sw/sw_information.cfg",
        "ltehost/config/sw/sw_manage.cfg",
        "ltehost/config/sw/sw_mirror.cfg",
        "ltehost/config/sw/sw_port.cfg",
        "ltehost/config/sw/sw_trunk.cfg",
        "ltehost/config/sw/sw_vlan.cfg",
        "ltehost/config/sw/sw_vlan_pvlan_list.cfg",
        "ltehost/config/sw/sw_vlan_qvlan_list.cfg",
        "ltehost/config/sw/sw_vlan_qvlan_port_vid.cfg",
        "ltehost/modemcfg1",
        "ltehost/modemcfg2",
        "ltehost/modemqos1",
        "ltehost/modemqos2",
        "ltehost/maincfg",
        "ltehost/catch.cfg",
        "ltehost/catch1.cfg",
        "ltehost/catch2.cfg",
        "ltehost/catch3.cfg",
        "ltehost/ntp.cfg",
        "ltehost/sntp.cfg",
        "ltehost/GREMulticast.cfg",
        "ltehost/gpfun.cfg",
        "ltehost/ztedtu.cfg",
        "ltehost/vlan.sh",
        "ltehost/restore_cfg",
        "etc/eth0-setting",
        "etc/eth0.1-setting",
        "etc/eth0.2-setting",
        "etc/udhcpd.conf",
        "etc/snmptrap_config",
        "etc/sysconfig/static-routes",
        "etc/pimd.conf",
        "etc/quagga/zebra.conf",
        "etc/quagga/ospfd.conf",
        "etc/keepalived/keepalived.conf",
        "etc/ipsec.conf",
        "etc/ipsec.secrets",
        "etc/ipsec.d",
        "etc/config/easycwmp",
        "etc/config/firewall",
        "etc/config/gre",
        "etc/config/hosts",
        "etc/config/ipsec",
        "etc/config/l2tp",
        "etc/config/network",
        "etc/config/ospf",
        "etc/config/pim",
        "etc/config/pimd",
        "etc/config/qos",
        "etc/config/route",
        "etc/config/snmp",
        "etc/config/syslog",
        "etc/config/time",
        "etc/config/vlan",
        "etc/config/vrrp",
        "usr/local/apache/htdocs/conf/user.xml",
        "usr/local/apache/htdocs/conf/features.xml",
        "usr/local/apache/htdocs/conf/menu.xml",
        "usr/local/apache/htdocs/conf/menu-all.xml",
        "usr/local/apache/htdocs/conf/settings.xml",
        "usr/local/apache/htdocs/conf/startup.conf",
        "usr/local/apache/htdocs/conf/vpnstartup.conf",
        "usr/local/apache/htdocs/conf/ipsec.conf",
        "usr/local/apache/htdocs/conf/pimd.conf",
        "usr/local/apache/htdocs/conf/rsa.conf",
        "usr/local/apache/htdocs/conf/scep_php.conf",
        "usr/local/apache/htdocs/conf/pki_php.conf",
        "usr/local/apache/htdocs/conf/snmpd.conf",
        "usr/local/apache/htdocs/conf/ntp.conf",
        "usr/local/apache/htdocs/conf/firewall",
        "usr/local/apache/htdocs/conf/iptables_port.conf",
        "usr/local/apache/htdocs/conf/lan",
        "usr/local/apache/htdocs/conf/monitor",
        "usr/local/apache/htdocs/conf/syslog",
        "usr/local/apache/htdocs/conf/firewall.cfg",
        "usr/local/apache/htdocs/conf/dhcp.conf",
        "usr/local/apache/htdocs/conf/GREMulticast.cfg",
        "usr/local/apache/htdocs/conf/ipsec",
        "usr/local/apache/htdocs/conf/vrrp",
        "tmp/tau_config_tool_state/snmp_cpu_check",
    ];

    public static IReadOnlyList<string> NonWirelessSupplementalPaths { get; } =
    [
        "etc/config/dhcp",
        "etc/config/dropbear",
        "etc/config/system",
        "etc/config/uhttpd",
        "etc/config/dataout",
        "etc/config/multicast",
        "etc/config/pis",
        "etc/config/pki",
        "etc/config/cert",
        "etc/config/staticroute",
        "etc/config/static-routes",
        "etc/config/switch",
        "etc/config/sw",
        "ltehost/config/pis.cfg",
        "ltehost/config/pim.cfg",
        "ltehost/config/multicast.cfg",
        "ltehost/config/route.cfg",
        "ltehost/config/staticroute.cfg",
        "ltehost/config/ospf.cfg",
        "ltehost/config/vrrp.cfg",
        "ltehost/config/gre.cfg",
        "ltehost/config/firewall.cfg",
        "usr/local/apache/htdocs/conf/cert",
        "usr/local/apache/htdocs/conf/pki",
        "usr/local/apache/htdocs/conf/crl",
        "usr/local/apache/htdocs/conf/gre",
        "usr/local/apache/htdocs/conf/multicast",
        "usr/local/apache/htdocs/conf/pim",
        "usr/local/apache/htdocs/conf/pis",
        "usr/local/apache/htdocs/conf/route",
        "usr/local/apache/htdocs/conf/ospf",
        "usr/local/apache/htdocs/conf/staticroute",
        "usr/local/apache/htdocs/conf/switch",
        "usr/local/apache/htdocs/conf/vlan",
        "usr/local/apache/htdocs/conf/dataout",
        "usr/local/apache/htdocs/conf/nat",
    ];

    public static IReadOnlyList<string> GeneratedProfilePaths { get; } =
        Enumerable.Range(1, MaxGreTunnelProfiles)
            .SelectMany(index => new[]
            {
                $"usr/local/apache/htdocs/conf/tunnel{index}.conf",
                $"ltehost/tunnel/tunnel{index}.conf",
            })
            .ToList();

    public static IReadOnlyList<string> GetExportPaths(ExportOptions options)
    {
        var paths = DefaultWhitelist
            .Concat(NonWirelessSupplementalPaths)
            .Concat(GeneratedProfilePaths)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (options.IncludeHardwareDetection)
        {
            paths.Add("ltehost/config/hw/hw_config.cfg");
        }

        if (options.IncludeLicenses)
        {
            paths.Add("etc/config/license");
            paths.Add("usr/local/apache/htdocs/conf/sct_tau.lic");
            paths.Add("usr/local/apache/htdocs/conf/license");
        }

        return paths;
    }
}
