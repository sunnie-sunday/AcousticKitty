// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.IO;
using AcousticKitty.Character;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Common;

internal static class UpdateMigrations
{
	public const int CurrentVersion = 3;

	private const string LegacyEchoStoreFileName = "verification-queue.db3";

	private static readonly string[] SqliteSidecarSuffixes = ["", "-wal", "-shm", "-journal"];

	public static void Run(
		Configuration configuration,
		string pluginConfigDirectory,
		CharacterDirectory characterDirectory,
		IPluginLog log)
	{
		if (configuration.Version >= UpdateMigrations.CurrentVersion)
		{
			return;
		}

		if (configuration.Version < 3)
		{
			UpdateMigrations.DeleteLegacyEchoStore(pluginConfigDirectory, log);
			var migrated = characterDirectory.MigrateLegacyGenderToCustomize();
			log.Information($"Migration v3: moved Gender into a blank Customize for {migrated} row(s).");
		}

		configuration.Version = UpdateMigrations.CurrentVersion;
		configuration.Save();
	}

	private static void DeleteLegacyEchoStore(string pluginConfigDirectory, IPluginLog log)
	{
		log.Information($"Migration v3: deleting the legacy {LegacyEchoStoreFileName}.");
		foreach (var suffix in SqliteSidecarSuffixes)
		{
			var path = Path.Combine(pluginConfigDirectory, LegacyEchoStoreFileName + suffix);
			try
			{
				File.Delete(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				log.Warning($"Migration v3: couldn't delete '{path}': {ex.Message}");
			}
		}
	}
}
