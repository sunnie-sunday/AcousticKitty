// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using AcousticKitty.Echo;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Common;

internal static class UpdateMigrations
{
	public const int CurrentVersion = 2;

	public static void Run(
		Configuration configuration,
		EchoStore echoStore,
		LodestoneCache lodestoneCache,
		CharacterDirectory characterDirectory,
		IPluginLog log)
	{
		if (configuration.Version >= UpdateMigrations.CurrentVersion)
		{
			return;
		}

		if (configuration.Version < 2)
		{
			UpdateMigrations.MigratePinsToLodestoneId(echoStore, lodestoneCache, characterDirectory, log);
		}

		configuration.Version = UpdateMigrations.CurrentVersion;
		configuration.Save();
	}

	private static void MigratePinsToLodestoneId(
		EchoStore echoStore,
		LodestoneCache lodestoneCache,
		CharacterDirectory characterDirectory,
		IPluginLog log)
	{
		log.Information("Migration v2: re-keying pinned/verified rows by Lodestone ID.");
		echoStore.MigrateKeysToLodestoneId(nameWorldKey =>
			lodestoneCache.TryGetResolvedId(nameWorldKey)
			?? characterDirectory.TryGetByNameWorldKey(nameWorldKey)?.LodestoneId);
	}
}
