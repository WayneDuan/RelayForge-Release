# Windows Multi-Port Forwarding

RelayForge exposes arbitrary Windows services behind NAT by using an existing public node as the entry node and the Windows machine as the internal node. Each forward maps one public entry port to one target address. Create as many forwards as needed on the same internal reverse relay tunnel.

1. Create a node for the public server and a node for the Windows machine. On the Windows node card, copy **Windows command** and run it in an elevated PowerShell session.
2. Create a tunnel with the public server as the entry node, the Windows node as the exit node, and select **Internal reverse relay**.
3. Create one forward for every port mapping. The target may be any address reachable from the Windows node, including `127.0.0.1:<port>` or another LAN host.

| Public entry port | Windows target | Example service |
| --- | --- | --- |
| `53389` | `127.0.0.1:3389` | Remote Desktop |
| `50080` | `127.0.0.1:80` | Web application |
| `50445` | `192.168.1.20:445` | LAN file service |

4. Allow each selected entry port through the public server firewall. Clients connect to `public-server-address:<public entry port>`.

The Windows node only makes an outbound connection to the public entry node. Do not expose internal service ports directly on the Windows router. Limit the public firewall source addresses where possible and apply service-specific access controls.
