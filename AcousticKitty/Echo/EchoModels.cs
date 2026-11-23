// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using SQLite;

namespace AcousticKitty.Echo;

public sealed record PinnedCharacter(
	ulong? ContentId,
	ulong LodestoneId,
	string NameWorldKey,
	string Name,
	uint HomeWorldId,
	string AvatarUrlHash,
	uint? JobId,
	int Level,
	ulong? FreeCompanyId,
	string? FreeCompanyName);

[Table("Pins")]
internal sealed class PinRow
{
	[PrimaryKey]
	public long LodestoneId { get; set; }

	[Indexed]
	public long? ContentId { get; set; }

	[NotNull]
	public string Name { get; set; } = string.Empty;

	[NotNull]
	public string HomeWorld { get; set; } = string.Empty;

	[NotNull]
	public string AvatarUrlHash { get; set; } = string.Empty;

	[NotNull]
	public string Job { get; set; } = string.Empty;

	public int Level { get; set; }

	public long? FreeCompanyId { get; set; }

	public string? FreeCompanyName { get; set; }
}
