# LAN Mode Troubleshooting

LAN Mode lets you view the map from another device (phone, tablet, laptop) on the same WiFi/network — useful if you don't have a second monitor. It's entirely local: nothing goes over the internet, and nothing outside your own network can reach it.

If you've turned on **LAN Mode**, clicked **Show IP**, typed that address into a browser on another device, and it's not working — this is a checklist of everything that can cause that, roughly in order of how often it turns out to be the actual cause.

## First, know what "not working" looks like

The exact symptom tells you a lot about where to look:

- **Stuck loading, no progress, no error** — the connection is being silently blocked somewhere between the two devices. This is the most common case, and points at network-level causes below (client isolation, firewall, VLANs).
- **"Server stopped responding" / "can't connect" / similar explicit error** — usually means the same thing as above, just a browser that reports it faster instead of hanging.
- **A certificate warning ("not private", "not secure")** — this is expected, not a bug. See [Certificate warnings](#certificate-warnings-are-expected) below.
- **Blank white page, no warning at all** — check the certificate warning didn't get dismissed/blocked by the browser silently; otherwise treat as a connection failure.

## Quick checklist

Try these first, roughly fastest-to-check first:

1. **Is the other device on the exact same WiFi network?** Not a guest network, not a different band with a different name (some routers split 2.4GHz/5GHz into separate SSIDs) — the *same* network the PC is connected to.
2. **Is ZoidHub's own network connection set to "Private" in Windows, not "Public"?** See [Windows network profile](#windows-network-profile-public-vs-private) below — this one is easy to get backwards.
3. **Did you click "Allow access" on the Windows Firewall prompt** when LAN Mode was first turned on? If you're not sure, see [Windows Firewall](#windows-firewall) below for how to check directly.
4. **Are you testing with the address itself**, typed directly into a browser — not through another app (see [A clean way to test](#a-clean-way-to-test) below, some apps quietly go over the internet even when you think they're testing your local network).

If all of those check out and it's still not working, it's very likely something on the network itself blocking device-to-device traffic — read on.

## Windows network profile (Public vs Private)

Windows classifies every network connection as either **Public** or **Private**. This matters because the Windows Firewall rule that lets other devices reach ZoidHub only applies when the connection is set to **Private** — on a **Public** connection, Windows blocks incoming connections by default, silently.

To check: Settings → Network & Internet → Wi-Fi → (your network) → **Network profile type**. For your home network, this should be **Private**, even though Windows often labels **Public** as "Recommended" (that's a reasonable general default for unfamiliar networks — home networks specifically should be Private).

Switching this takes effect immediately — no restart needed. Try connecting again right after switching.

## Windows Firewall

When LAN Mode is turned on for the first time, Windows shows a firewall permission prompt. If "Allow access" wasn't clicked, or only "Public networks" was checked (not "Private networks"), incoming connections get silently blocked.

If you have access to PowerShell, you can check the actual rule directly rather than guessing:

```powershell
Get-NetFirewallRule -DisplayName "*ZoidHub*" | Select-Object DisplayName, Direction, Action, Enabled, Profile
```

You're looking for an **Inbound**, **Allow** rule, **Enabled: True**, for the **Private** profile. If it's missing or disabled, removing it and re-triggering the firewall prompt (toggle LAN Mode off and on) should recreate it.

## Router/AP client isolation

Many WiFi routers and access points have a setting often called **"client isolation"** or **"AP isolation"** — it stops devices connected to the same WiFi from talking to each other directly, even though they're all on the same network and can all reach the internet fine. This is common on guest networks, and sometimes enabled by default even on a main network.

This is a genuinely common real-world cause — worth checking specifically, since it produces exactly the "stuck loading, no progress, no error" symptom and won't show up in any Windows-side check at all, since the block happens before traffic ever reaches the PC.

Where to find it varies by router/AP brand, but look for "client isolation," "AP isolation," or "isolate clients" in your router or access point's WiFi settings.

## VLAN / segmented networks

If your home network is more advanced — separate VLANs for different device types, a dedicated firewall/router (like OPNsense, pfSense, or similar) — it's possible for a rule to block traffic between whatever network segment your phone/tablet is on and the one the PC is on, even if both look like they're on "the same" network at a glance.

If you manage this yourself, the specific thing to check for is a rule affecting **TCP port 41414** between the relevant segments. If someone else manages it, this is the point to loop them in — armed with the specific symptom ("device X can reach the router/gateway fine, but can't reach the PC's IP on port 41414 at all") rather than a vague "it doesn't work," which makes it much faster for them to find.

## iOS: iCloud Private Relay

If Private Relay is turned on (an iCloud+ feature — Settings → [your name] → iCloud → Private Relay), Safari routes traffic through Apple's own relay servers, which cannot reach a private local network address at all. This affects Safari and any other iOS browser too, since every iOS browser is required to use the same underlying engine as Safari.

Turn it off (at least for your home WiFi network) and try again. If you don't have iCloud+, this isn't available to turn on in the first place, so it can't be the cause.

## Certificate warnings are expected

LAN Mode uses HTTPS with a self-signed certificate, since a private local IP address can never get a certificate a browser trusts automatically (public certificate providers only issue for real domain names). The first time any device connects, the browser will show a "not private" or "not secure" warning — this is expected, not a sign anything is broken.

Click through it (usually "Advanced" → "Proceed anyway," wording varies by browser). Most browsers remember this choice for that device going forward, so it's typically a one-time thing per device.

## Third-party antivirus / firewall software

If you run a security suite other than Windows' own built-in protection (Norton, McAfee, Bitdefender, etc.), it may run its own separate firewall with its own separate rules that Windows Firewall's settings don't control. If everything above checks out and it's still not working, check whether such software is installed and whether it has its own blocking rules for ZoidHub or for port 41414.

## A clean way to test

If you want to independently confirm whether it's a network problem or something else, don't rely on another app (like a media server app) "working" as proof the network is fine — many apps quietly fall back to routing through the internet even when both devices are on the same WiFi, which would make the test meaningless. A clean test is to type a known local address directly into a browser — the same way you'd type ZoidHub's address — and see if that works. If it does, and ZoidHub still doesn't, that narrows things down to something specific to ZoidHub's port/connection rather than the network in general.

## Still stuck?

Open an issue on GitHub with what you've tried from this list and what symptom you're seeing (see the main README for the link) - the app log at `%AppData%\ZoidHub\logs\zoidhub.log` is worth including too.
