// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Common;

public static class CharacterTransferRecorder
{
	public static void Record(
		CharacterDirectory characterDirectory,
		LodestoneCache lodestoneCache,
		IDataManager dataManager,
		ulong contentId,
		string oldName,
		uint oldHomeWorldId,
		DateTime oldLastSeenUtc,
		string newName,
		uint newHomeWorldId,
		ulong? lodestoneId)
	{
		var previousWorldName = GameDataResolver.ResolveWorldName(dataManager, oldHomeWorldId);
		characterDirectory.RecordNameHistory(contentId, oldName, previousWorldName, oldLastSeenUtc);

		if (lodestoneId == null)
		{
			return;
		}

		var oldKey = CharacterKey.Build(oldName, oldHomeWorldId);
		var newKey = CharacterKey.Build(newName, newHomeWorldId);

		lodestoneCache.RenameCharacter(oldKey, newKey, newName, newHomeWorldId);

		characterDirectory.SetLookupResult(
			contentId, lodestoneId, NearbyLookupState.Pending, null, priorityAtUtc: DateTime.UtcNow);
	}
}
