// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

namespace AcousticKitty.Character;

public sealed record PlayerLocalData(
	string Name,
	uint HomeWorldId,
	ulong ContentId,
	uint JobId,
	int Level,
	string? FreeCompanyTag,
	uint TitleId,
	uint TerritoryId,
	uint CurrentWorldId,
	string Gender);
