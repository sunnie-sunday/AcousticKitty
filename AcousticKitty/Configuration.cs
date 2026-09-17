// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
using Dalamud.Configuration;

namespace AcousticKitty;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
	public int Version { get; set; } = 1;

	public const string DefaultLodestoneUserAgent =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
		"Chrome/152.0.0.0 Safari/537.36 Edg/152.0.4191.66";

	public string LodestoneUserAgent { get; set; } = DefaultLodestoneUserAgent;

	public NearbySearchMode NearbySearchMode { get; set; } = NearbySearchMode.Manual;

	public void Save()
	{
		Plugin.PluginInterface.SavePluginConfig(this);
	}
}
