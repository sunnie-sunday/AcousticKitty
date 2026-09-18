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

			var infoList = entry.SelectSingleNode(
				$".//ul[{HasClass("entry__freecompany__info")} or {HasClass("entry__chara_info")}]");
			var classJobIcon = infoList
				?.SelectSingleNode($".//li[.//i[{HasClass("list__ic__class")}]]");
			var levelText = classJobIcon?.SelectSingleNode(".//span")?.InnerText;
			var jobIconUrl = classJobIcon?.SelectSingleNode(".//img")?.GetAttributeValue("src", null);

			var (world, _) = SplitWorldDataCenter(CleanText(worldNode.InnerText) ?? string.Empty);
			var worldId = GameDataResolver.TryResolveWorldId(dataManager, world)
				?? throw new LodestoneDataResolutionException(world);

			var level = int.TryParse(CleanText(levelText), out var parsedLevel) ? parsedLevel : (int?)null;
			var jobAbbreviation = ResolveClassJobAbbreviation(jobIconUrl);
			var jobId = jobAbbreviation != null
				? GameDataResolver.TryResolveJobId(dataManager, jobAbbreviation)
				: null;

			results.Add(new MemberListEntry(
				CharacterId: id,
				Name: CleanText(nameNode.InnerText) ?? string.Empty,
				HomeWorldId: worldId,
				AvatarUrlHash: avatarUrlHash,
				Level: level,
				JobId: jobId,
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
		var title = CleanText(SelectText(root, $"//p[{HasClass("frame__chara__title")}]"));
		var worldText = CleanText(SelectText(root, $"//p[{HasClass("frame__chara__world")}]"))
			?? string.Empty;
		var avatarUrlHash = AvatarUrlBuilder.ExtractHash(StripQueryString(root
			.SelectSingleNode($"//div[{HasClass("frame__chara__face")}]/img")
			?.GetAttributeValue("src", null)));

		var (world, _) = SplitWorldDataCenter(worldText);
		var worldId = GameDataResolver.TryResolveWorldId(dataManager, world)
			?? throw new LodestoneDataResolutionException(world);
		var isPartiallyPrivate = IsProfilePrivate(root);

		var rawGender = ParseGender(GetBlockValueNode(root, "Race/Clan/Gender"));
		var gender = LodestoneParser.NormalizeGenderSymbol(rawGender);
		var titleId = ResolveOptionalId(
			title ?? string.Empty, text => GameDataResolver.TryResolveTitleId(dataManager, text, gender));

		var fcLink = root.SelectSingleNode($"//div[{HasClass("character__freecompany__name")}]/h4/a");
		var freeCompanyName = CleanText(fcLink?.InnerText);
		var freeCompanyId = ParseTrailingId(fcLink?.GetAttributeValue("href", null), "freecompany");

		var bioNode = root.SelectSingleNode($"//div[{HasClass("character__selfintroduction")}]");
		var bio = bioNode != null ? ParseBioCode(bioNode.InnerHtml) : null;

		var classDataNode = root.SelectSingleNode($"//div[{HasClass("character__class__data")}]");
		var (jobId, level) = ParseCurrentClassJobLevel(dataManager, classDataNode);

		return new LodestoneProfile(
			LodestoneId: lodestoneId,
			Name: name,
			TitleId: titleId,
			HomeWorldId: worldId,
			AvatarUrlHash: avatarUrlHash,
			IsPartiallyPrivate: isPartiallyPrivate,
			Gender: gender,
			FreeCompanyId: freeCompanyId,
			FreeCompanyName: string.IsNullOrEmpty(freeCompanyName) ? null : freeCompanyName,
			Bio: bio,
			JobId: jobId,
			Level: level);
	}

	#endregion

	#region Private Helpers

	private static readonly Regex WorldDataCenterRegex =
		new(@"^(?<world>.+?)\s*\[(?<dc>.+?)\]$", RegexOptions.Compiled);
	private static readonly Regex BrTagRegex =
		new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex AnyTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
	private static readonly Regex CurrentLevelRegex = new(@"\d+", RegexOptions.Compiled);

	private static readonly IReadOnlyDictionary<string, string> ClassJobIconFileNameToAbbreviation =
		new Dictionary<string, string>
	{
		["d0Tx-vhnsMYfYpGe9MvslemEfg.png"] = "PLD",
		["A3UhbjZvDeN3tf_6nJ85VP0RY0.png"] = "WAR",
		["5CZEvDOMYMyVn2td9LZigsgw9s.png"] = "DRK",
		["hg8ofSSOKzqng290No55trV4mI.png"] = "GNB",
		["i20QvSPcSQTybykLZDbQCgPwMw.png"] = "WHM",
		["WdFey0jyHn9Nnt1Qnm-J3yTg5s.png"] = "SCH",
		["erCgjnMSiab4LiHpWxVc-tXAqk.png"] = "AST",
		["_oYApASVVReLLmsokuCJGkEpk0.png"] = "SGE",
		["HW6tKOg4SOJbL8Z20GnsAWNjjM.png"] = "MNK",
		["gX4OgBIHw68UcMU79P7LYCpldA.png"] = "DRG",
		["Fso5hanZVEEAaZ7OGWJsXpf3jw.png"] = "NIN",
		["KndG72XtCFwaq1I1iqwcmO_0zc.png"] = "SAM",
		["cLlXUaeMPJDM2nBhIeM-uDmPzM.png"] = "RPR",
		["WojNTqMJ_Ye1twvkIhw825zc20.png"] = "VPR",
		["OFfyg3oiCvw6DQmECas-wZv01A.png"] = "BST",
		["KWI-9P3RX_Ojjn_mwCS2N0-3TI.png"] = "BRD",
		["vmtbIlf6Uv8rVp2YFCWA25X0dc.png"] = "MCH",
		["HK0jQ1y7YV9qm30cxGOVev6Cck.png"] = "DNC",
		["V01m8YRBYcIs5vgbRtpDiqltSE.png"] = "BLM",
		["4ghjpyyuNelzw1Bl0sM_PBA_FE.png"] = "SMN",
		["s3MlLUKmRAHy0pH57PnFStHmIw.png"] = "RDM",
		["kLob-U-yh652LQPX1NHpLlUYQY.png"] = "PCT",
		["jdV3RRKtWzgo226CC09vjen5sk.png"] = "BLU",
		["YCN6F-xiXf03Ts3pXoBihh2OBk.png"] = "CRP",
		["EEHVV5cIPkOZ6v5ALaoN5XSVRU.png"] = "BSM",
		["Rq5wcK3IPEaAB8N-T9l6tBPxCY.png"] = "ARM",
		["LbEjgw0cwO_2gQSmhta9z03pjM.png"] = "GSM",
		["ACAcQe3hWFxbWRVPqxKj_MzDiY.png"] = "LTW",
		["E69jrsOMGFvFpCX87F5wqgT_Vo.png"] = "WVR",
		["bBVQ9IFeXqjEdpuIxmKvSkqalE.png"] = "ALC",
		["1kMI2v_KEVgo30RFvdFCyySkFo.png"] = "CUL",
		["aM2Dd6Vo4HW_UGasK7tLuZ6fu4.png"] = "MIN",
		["jGRnjIlwWridqM-mIPNew6bhHM.png"] = "BTN",
		["B4Azydbn7Prubxt7OL9p1LZXZ0.png"] = "FSH",

		["ZpqEJWYHj9SvHGuV9cIyRNnIkk.png"] = "ARC",
		["iW7IBKQ7oglB9jmbn6LwdZXkWw.png"] = "PGL",
		["F5JzG9RPIKFSogtaKNBk455aYA.png"] = "GLA",
		["IM3PoP6p06GqEyReygdhZNh7fU.png"] = "THM",
		["St9rjDJB3xNKGYg-vwooZ4j6CM.png"] = "MRD",
		["VYP1LKTDpt8uJVvUT7OKrXNL9E.png"] = "ACN",
		["tYTpoSwFLuGYGDJMff8GEFuDQs.png"] = "LNC",
		["gl62VOTBJrm7D_BmAZITngUEM8.png"] = "CNJ",
		["wdwVVcptybfgSruoh8R344y_GA.png"] = "ROG",
	};

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

	private static uint? ResolveOptionalId(string text, Func<string, uint?> resolve) =>
		string.IsNullOrEmpty(text)
			? null
			: resolve(text) ?? throw new LodestoneDataResolutionException(text);

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

	private static string? ResolveClassJobAbbreviation(string? iconUrl)
	{
		if (string.IsNullOrEmpty(iconUrl))
		{
			return null;
		}

		var fileName = iconUrl[(iconUrl.LastIndexOf('/') + 1)..];
		return ClassJobIconFileNameToAbbreviation.TryGetValue(fileName, out var name) ? name : null;
	}

	private static (uint? JobId, int? Level) ParseCurrentClassJobLevel(
		IDataManager dataManager, HtmlNode? classDataNode)
	{
		if (classDataNode == null)
		{
			return (null, null);
		}

		var levelText = CleanText(classDataNode.SelectSingleNode(".//p")?.InnerText);
		var levelMatch = levelText != null ? CurrentLevelRegex.Match(levelText) : Match.Empty;
		var level = levelMatch.Success && int.TryParse(levelMatch.Value, out var parsedLevel)
			? parsedLevel
			: (int?)null;

		var iconUrl = classDataNode
			.SelectSingleNode($".//div[{HasClass("character__class_icon")}]/img")
			?.GetAttributeValue("src", null);
		var jobAbbreviation = ResolveClassJobAbbreviation(iconUrl);
		var jobId = jobAbbreviation != null
			? GameDataResolver.TryResolveJobId(dataManager, jobAbbreviation)
			: null;

		return (jobId, level);
	}

	private static bool IsProfilePrivate(HtmlNode root)
	{
		var xpath = $"//p[{HasClass("parts__zero")} and contains(text(), \"profile is private\")]";
		return root.SelectSingleNode(xpath) != null;
	}

	private static HtmlNode? GetBlockValueNode(HtmlNode root, string titleText)
	{
		var titleNode = root.SelectSingleNode(
			$".//p[{HasClass("character-block__title")} and normalize-space(text())='{titleText}']");
		if (titleNode == null)
		{
			return null;
		}

		var sibling = titleNode.NextSibling;
		while (sibling != null && sibling.NodeType != HtmlNodeType.Element)
		{
			sibling = sibling.NextSibling;
		}

		return sibling;
	}

	private static string NormalizeGenderSymbol(string symbol) => symbol switch
	{
		"♂" => "M",
		"♀" => "F",
		_ => symbol,
	};

	private static string ParseGender(HtmlNode? valueNode)
	{
		if (valueNode == null)
		{
			return string.Empty;
		}

		var text = CleanMultilineHtml(valueNode.InnerHtml);
		var splitOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
		var lines = text.Split('\n', splitOptions);
		var (_, gender) = lines.Length > 1 ? SplitSlashPair(lines[1]) : (null, null);
		return gender ?? string.Empty;
	}

	private static string? ParseBioCode(string innerHtml)
	{
		var text = CleanMultilineHtml(innerHtml);
		var splitOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
		var lines = text.Split('\n', splitOptions);

		return Array.Find(lines, line => line.Contains("ECHO", StringComparison.Ordinal))
			?? Array.Find(lines, line => line.Contains("echo", StringComparison.Ordinal));
	}

	private static (string? First, string? Second) SplitSlashPair(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return (null, null);
		}

		var parts = text.Split(" / ", 2, StringSplitOptions.TrimEntries);
		return parts.Length == 2 ? (parts[0], parts[1]) : (text, null);
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

	private static string CleanMultilineHtml(string innerHtml)
	{
		var withNewlines = BrTagRegex.Replace(innerHtml, "\n");
		var decoded = HtmlEntity.DeEntitize(withNewlines);
		var stripped = AnyTagRegex.Replace(decoded, string.Empty);
		return stripped.Trim();
	}

	#endregion
}
