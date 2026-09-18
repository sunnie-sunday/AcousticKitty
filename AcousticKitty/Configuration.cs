// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
using AcousticKitty.Echo;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace AcousticKitty;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
	public int Version { get; set; } = 1;

	public const string DefaultLodestoneUserAgent =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
		"Chrome/152.0.0.0 Safari/537.36 Edg/152.0.4191.66";

	public string LodestoneUserAgent { get; set; } = DefaultLodestoneUserAgent;

	public ProxyMode EchoProxyMode { get; set; } = ProxyMode.Socks5;

	public string EchoProxyHost { get; set; } = "localhost";

	public int EchoProxyPort { get; set; } = 1080;

	public string EchoProxyUsername { get; set; } = string.Empty;

	public string EchoProxyEncryptedPassword { get; set; } = string.Empty;

	[JsonIgnore]
	public string EchoProxyPassword
	{
		get => DpapiProtector.Unprotect(this.EchoProxyEncryptedPassword);
		set => this.EchoProxyEncryptedPassword = DpapiProtector.Protect(value);
	}

	public string EchoApiHost { get; set; } = "http://localhost";

	public string EchoPluginVersion { get; set; } = "0.0.1";

	public int EchoProtocolVersion { get; set; } = 2;

	public NearbySearchMode NearbySearchMode { get; set; } = NearbySearchMode.Manual;

	public EchoQueueMode EchoQueueMode { get; set; } = EchoQueueMode.Auto;

	public void Save()
	{
		Plugin.PluginInterface.SavePluginConfig(this);
	}
}
