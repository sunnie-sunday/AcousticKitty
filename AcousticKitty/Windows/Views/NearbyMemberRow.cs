// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using Dalamud.Bindings.ImGui;

namespace AcousticKitty.Windows.Views;

internal static class NearbyMemberRow
{
	public static void Draw(
		Plugin plugin, NearbyCharactersService nearby, NearbyMemberViewModel member,
		Dictionary<string, string> lodestoneIdInputs)
	{
		var data = member.KnownCharacter.Data;

		var primaryLine = ProfileView.FormatPrimaryLine(
			data, member.Profile, data.Name, member.WorldName, member.DataCenterName);
		var badge = ProfileView.FormatJobLine(
			data, member.Profile, member.JobAbbreviation, data.Level.ToString());

		var statusMessage = member.KnownCharacter.LookupState switch
		{
			NearbyLookupState.NotFound =>
				"This character is hidden from search results.",
			NearbyLookupState.AccessRestricted =>
				"This character's profile is private.",
			NearbyLookupState.Pending when member.KnownCharacter.LookupErrorMessage != null =>
				$"Last attempt failed, will retry: {member.KnownCharacter.LookupErrorMessage}",
			NearbyLookupState.Error =>
				"Lookup failed, add a Lodestone ID to try again.",
			_ => null,
		};

		var (rowStartX, rowStartY, availWidth) = RowOverlay.CaptureStart();

		MemberRow.Draw(
			plugin,
			new CharacterRowData(
				data.ContentId.ToString(),
				member.IsPinned,
				ProfileView.ResolveAvatarUrl(member.Profile, member.AvatarUrlHash, member.WorldName),
				primaryLine,
				badge,
				IsBusy: member.IsSearching,
				statusMessage,
				member.Profile is { } profile ? new Lazy<LodestoneProfile>(() => profile) : null,
				data,
				member.KnownCharacter.LastSeenUtc,
				new Lazy<IReadOnlyList<NameHistoryEntry>>(() => nearby.GetNameHistory(data.ContentId)),
				member.IsVerified,
				member.ProfileAsOfUtc));

		var rowEndY = ImGui.GetCursorPosY();

		if (member.KnownCharacter.LookupState != NearbyLookupState.Found)
		{
			LodestoneOverride.Draw(
				lodestoneIdInputs, data.ContentId.ToString(), data.Name, data.FreeCompanyTag,
				member.WorldName, data.ContentId, nearby.QueueOverride, rowStartX, rowStartY,
				availWidth);
		}

		RowOverlay.RestoreBelow(rowEndY);
	}
}
