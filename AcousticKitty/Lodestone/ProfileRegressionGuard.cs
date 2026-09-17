// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

namespace AcousticKitty.Lodestone;

internal static class ProfileRegressionGuard
{
	public static bool IsRegression(LodestoneProfile newProfile, LodestoneProfile previous) =>
		newProfile.IsPartiallyPrivate && !previous.IsPartiallyPrivate;

	public static string LogMessage(string characterName) =>
		$"Discarded a refreshed profile for {characterName} - it had less data than what was " +
		"already cached (possible Lodestone maintenance or a privacy change); kept the previous " +
		"data.";
}
