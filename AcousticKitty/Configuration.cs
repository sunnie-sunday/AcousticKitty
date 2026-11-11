// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
using AcousticKitty.Common;
using Dalamud.Configuration;

namespace AcousticKitty;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
	public int Version { get; set; } = 1;

	public NearbySearchMode NearbySearchMode { get; set; } = NearbySearchMode.Manual;
	public DatabaseCacheTier DatabaseCacheTier { get; set; } = DatabaseCacheTier.Medium;

	public void Save()
	{
		Plugin.PluginInterface.SavePluginConfig(this);
	}
}
