// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AcousticKitty.Common;
using Dalamud.Plugin.Services;
using HtmlAgilityPack;

namespace AcousticKitty.Lodestone;

public static class LodestoneParser
{
	#region Public Parsing Methods

	public static IReadOnlyList<MemberListEntry> ParseMemberListEntries(
		IDataManager dataManager, string html)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);

		var entryLinks = doc.DocumentNode.SelectNodes(
			$"//*[{HasClass("entry")}]/a[{HasClass("entry__link")} or {HasClass("entry__bg")}]");
		if (entryLinks == null)
		{
			return Array.Empty<MemberListEntry>();
		}

		var results = new List<MemberListEntry>(entryLinks.Count);
		foreach (var entry in entryLinks)
		{
			var nameNode = entry.SelectSingleNode($".//p[{HasClass("entry__name")}]");
			var worldNode = entry.SelectSingleNode($".//p[{HasClass("entry__world")}]");
			if (nameNode == null || worldNode == null)
			{
				continue;
			}

			var href = entry.GetAttributeValue("href", string.Empty);
			var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
			var characterIndex = Array.IndexOf(segments, "character");
			if (characterIndex < 0 || characterIndex + 1 >= segments.Length ||
				!ulong.TryParse(segments[characterIndex + 1], out var id))
			{
				continue;
			}

			var avatarUrlHash = AvatarUrlBuilder.ExtractHash(StripQueryString(entry
				.SelectSingleNode($".//div[{HasClass("entry__chara__face")}]/img")
				?.GetAttributeValue("src", null)));

			var fcLink = entry.ParentNode
				?.SelectSingleNode($"./a[{HasClass("entry__freecompany__link")}]");
			var freeCompanyId = ParseTrailingId(fcLink?.GetAttributeValue("href", null), "freecompany");
			var freeCompanyName = CleanText(fcLink?.SelectSingleNode(".//span")?.InnerText);

			var (world, _) = SplitWorldDataCenter(CleanText(worldNode.InnerText) ?? string.Empty);
			var worldId = GameDataResolver.TryResolveWorldId(dataManager, world)
				?? throw new LodestoneDataResolutionException(world);

			results.Add(new MemberListEntry(
				CharacterId: id,
				Name: CleanText(nameNode.InnerText) ?? string.Empty,
				HomeWorldId: worldId,
				AvatarUrlHash: avatarUrlHash,
				FreeCompanyId: freeCompanyId,
				FreeCompanyName: string.IsNullOrEmpty(freeCompanyName) ? null : freeCompanyName));
		}

		return results;
	}

	public static MemberListEntry? FindCharacterEntry(
		IDataManager dataManager,
		string searchResultsHtml,
		string characterName,
		string worldName)
	{
		var expectedWorldId = GameDataResolver.TryResolveWorldId(dataManager, worldName)
			?? throw new LodestoneDataResolutionException(worldName);

		foreach (var entry in ParseMemberListEntries(dataManager, searchResultsHtml))
		{
			if (string.Equals(entry.Name, characterName, StringComparison.Ordinal) &&
				entry.HomeWorldId == expectedWorldId)
			{
				return entry;
			}
		}

		return null;
	}

	public static IReadOnlyList<GroupSearchResult> ParseGroupSearchResults(
		IDataManager dataManager,
		string html,
		SocialGroupKind kind)
	{
		var (anchorClass, boxClass, urlSegment) = kind switch
		{
			SocialGroupKind.FreeCompany => ("entry__block", "entry__freecompany__box", "freecompany"),
			SocialGroupKind.PvpTeam => ("entry__block", "entry__freecompany__box", "pvpteam"),
			SocialGroupKind.Linkshell => ("entry__link--line", "entry__linkshell", "linkshell"),
			SocialGroupKind.CrossWorldLinkshell =>
				("entry__link--line", "entry__linkshell", "crossworld_linkshell"),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
		};

		var sharedIconUrl = kind switch
		{
			SocialGroupKind.Linkshell => LinkshellIconUrl,
			SocialGroupKind.CrossWorldLinkshell => CrossWorldLinkshellIconUrl,
			_ => null,
		};

		var doc = new HtmlDocument();
		doc.LoadHtml(html);

		var entryLinks = doc.DocumentNode.SelectNodes(
			$"//div[{HasClass("entry")}]/a[{HasClass(anchorClass)}]");
		if (entryLinks == null)
		{
			return Array.Empty<GroupSearchResult>();
		}

		var results = new List<GroupSearchResult>(entryLinks.Count);
		foreach (var entry in entryLinks)
		{
			var nameNode = entry.SelectSingleNode(
				$".//div[{HasClass(boxClass)}]/p[{HasClass("entry__name")}]");
			var worldNodes = entry.SelectNodes(
				$".//div[{HasClass(boxClass)}]/p[{HasClass("entry__world")}]");
			if (nameNode == null || worldNodes == null || worldNodes.Count == 0)
			{
				continue;
			}

			var candidateWorldTexts = worldNodes
				.Select(n => CleanText(n.InnerText) ?? string.Empty)
				.ToList();
			var worldText = candidateWorldTexts.FirstOrDefault(t => WorldDataCenterRegex.IsMatch(t))
				?? candidateWorldTexts[^1];

			var href = entry.GetAttributeValue("href", string.Empty);
			var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
			var groupIndex = Array.IndexOf(segments, urlSegment);
			if (groupIndex < 0 || groupIndex + 1 >= segments.Length)
			{
				continue;
			}

			var name = CleanText(nameNode.InnerText) ?? string.Empty;
			var (world, dataCenterText) = SplitGroupWorldOrDataCenter(worldText);
			var worldId = world != null
				? GameDataResolver.TryResolveWorldId(dataManager, world)
					?? throw new LodestoneDataResolutionException(world)
				: (uint?)null;
			var dataCenterId = GameDataResolver.TryResolveDataCenterId(dataManager, dataCenterText)
				?? throw new LodestoneDataResolutionException(dataCenterText);
			IReadOnlyList<string> iconUrls = sharedIconUrl != null
				? new[] { sharedIconUrl }
				: ParseGroupCrestLayers(entry, kind);
			results.Add(new GroupSearchResult(
				segments[groupIndex + 1], name, worldId, dataCenterId, iconUrls));
		}

		return results;
	}

	public static int ParsePageCount(string html)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);

		var pagerText = doc.DocumentNode
			.SelectSingleNode($"//li[{HasClass("btn__pager__current")}]")
			?.InnerText;
		if (pagerText == null)
		{
			return 1;
		}

		var match = Regex.Match(pagerText, @"of\s+(\d+)");
		return match.Success && int.TryParse(match.Groups[1].Value, out var totalPages)
			? totalPages
			: 1;
	}

	public static string? ParseFreeCompanyName(string html)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);

		return CleanText(SelectText(doc.DocumentNode, $"//p[{HasClass("entry__freecompany__name")}]"));
	}

	public static LodestoneProfile ParseProfile(
		IDataManager dataManager,
		string profileHtml,
		ulong lodestoneId)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(profileHtml);
		var root = doc.DocumentNode;

		var name = CleanText(SelectText(root, $"//p[{HasClass("frame__chara__name")}]"))
			?? string.Empty;
		var worldText = CleanText(SelectText(root, $"//p[{HasClass("frame__chara__world")}]"))
			?? string.Empty;
		var avatarUrlHash = AvatarUrlBuilder.ExtractHash(StripQueryString(root
			.SelectSingleNode($"//div[{HasClass("frame__chara__face")}]/img")
			?.GetAttributeValue("src", null)));

		var (world, _) = SplitWorldDataCenter(worldText);
		var worldId = GameDataResolver.TryResolveWorldId(dataManager, world)
			?? throw new LodestoneDataResolutionException(world);
		var isPartiallyPrivate = IsProfilePrivate(root);

		var fcLink = root.SelectSingleNode($"//div[{HasClass("character__freecompany__name")}]/h4/a");
		var freeCompanyName = CleanText(fcLink?.InnerText);
		var freeCompanyId = ParseTrailingId(fcLink?.GetAttributeValue("href", null), "freecompany");

		return new LodestoneProfile(
			LodestoneId: lodestoneId,
			Name: name,
			HomeWorldId: worldId,
			AvatarUrlHash: avatarUrlHash,
			IsPartiallyPrivate: isPartiallyPrivate,
			FreeCompanyId: freeCompanyId,
			FreeCompanyName: string.IsNullOrEmpty(freeCompanyName) ? null : freeCompanyName);
	}

	#endregion

	#region Private Helpers

	private static readonly Regex WorldDataCenterRegex =
		new(@"^(?<world>.+?)\s*\[(?<dc>.+?)\]$", RegexOptions.Compiled);

	private static string HasClass(string className) =>
		$"contains(concat(' ', normalize-space(@class), ' '), ' {className} ')";

	private const string LinkshellIconUrl =
		"https://lds-img.finalfantasyxiv.com/h/J/sdHWnoNaGTGn6iGfhTZVBf4Y3Q.png";
	private const string CrossWorldLinkshellIconUrl =
		"https://lds-img.finalfantasyxiv.com/h/5/4_6qlZUYui4tW5ktSgjd-uYbxk.png";

	private static IReadOnlyList<string> ParseGroupCrestLayers(HtmlNode entry, SocialGroupKind kind)
	{
		var (baseClass, layerClass) = kind == SocialGroupKind.PvpTeam
			? ("entry__pvpteam__search__crest__base", "entry__pvpteam__search__crest__image")
			: ("entry__freecompany__crest__base", "entry__freecompany__crest__image");

		var layers = new List<string>();
		var baseUrl = entry.SelectSingleNode($".//img[{HasClass(baseClass)}]")
			?.GetAttributeValue("src", null);
		if (baseUrl != null)
		{
			layers.Add(baseUrl);
		}

		var layerNodes = entry.SelectNodes($".//div[{HasClass(layerClass)}]/img");
		if (layerNodes != null)
		{
			layers.AddRange(layerNodes
				.Select(node => node.GetAttributeValue("src", null))
				.Where(url => url != null)!);
		}

		return layers;
	}

	private static ulong? ParseTrailingId(string? href, string segment)
	{
		if (string.IsNullOrEmpty(href))
		{
			return null;
		}

		var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var index = Array.IndexOf(segments, segment);
		if (index < 0 || index + 1 >= segments.Length)
		{
			return null;
		}

		return ulong.TryParse(segments[index + 1], out var id) ? id : null;
	}

	private static bool IsProfilePrivate(HtmlNode root)
	{
		var xpath = $"//p[{HasClass("parts__zero")} and contains(text(), \"profile is private\")]";
		return root.SelectSingleNode(xpath) != null;
	}

	private static (string World, string DataCenter) SplitWorldDataCenter(string worldText)
	{
		var match = WorldDataCenterRegex.Match(worldText);
		return match.Success
			? (match.Groups["world"].Value, match.Groups["dc"].Value)
			: (worldText, string.Empty);
	}

	private static (string? World, string DataCenter) SplitGroupWorldOrDataCenter(string text)
	{
		var match = WorldDataCenterRegex.Match(text);
		return match.Success
			? (match.Groups["world"].Value, match.Groups["dc"].Value)
			: (null, text);
	}

	private static string? SelectText(HtmlNode root, string xpath) =>
		root.SelectSingleNode(xpath)?.InnerText;

	private static string? StripQueryString(string? url) => url?.Split('?', 2)[0];

	private static string? CleanText(string? rawInnerText)
	{
		if (rawInnerText == null)
		{
			return null;
		}

		return HtmlEntity.DeEntitize(rawInnerText).Trim();
	}

	#endregion
}
