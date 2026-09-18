// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;

namespace AcousticKitty.Windows.Views;

internal static class ProfileView
{
	public static string? ResolveAvatarUrl(
		LodestoneProfile? profile, string? fallbackAvatarUrlHash, string worldName) =>
		AvatarUrlBuilder.Build(profile?.AvatarUrlHash ?? fallbackAvatarUrlHash, worldName);

	public static void DrawAvatar(Plugin plugin, string? avatarUrl, float size)
	{
		IDalamudTextureWrap? texture = null;
		if (!string.IsNullOrEmpty(avatarUrl))
		{
			var textureTask = plugin.AvatarTextureCache.GetOrFetchAsync(avatarUrl, CancellationToken.None);
			if (textureTask.IsCompletedSuccessfully)
			{
				texture = textureTask.Result;
			}
		}

		if (texture != null)
		{
			ImGui.Image(texture.Handle, new Vector2(size, size));
		}
		else
		{
			ImGui.Dummy(new Vector2(size, size));
		}
	}

	public static void DrawLayeredIcon(Plugin plugin, IReadOnlyList<string> iconUrls, float size)
	{
		var origin = ImGui.GetCursorScreenPos();
		var drewAny = false;
		foreach (var url in iconUrls)
		{
			var textureTask = plugin.AvatarTextureCache.GetOrFetchAsync(url, CancellationToken.None);
			if (textureTask.IsCompletedSuccessfully && textureTask.Result is { } texture)
			{
				ImGui.SetCursorScreenPos(origin);
				ImGui.Image(texture.Handle, new Vector2(size, size));
				drewAny = true;
			}
		}

		if (drewAny)
		{
			ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + size));
		}
		else
		{
			ImGui.Dummy(new Vector2(size, size));
		}
	}

	public static string FormatNameLine(string name, string? title) =>
		title != null ? $"{name}  <{title}>" : name;

	public static string FormatPrimaryLine(
		string name, string? title, string world, string? dataCenter)
	{
		var worldLine = dataCenter != null ? $"{world} [{dataCenter}]" : world;
		return $"{ProfileView.FormatNameLine(name, title)}\n{worldLine}";
	}

	public static string FormatPrimaryLine(LodestoneProfile profile)
	{
		var dataManager = Plugin.DataManager;
		var world = GameDataResolver.ResolveWorldName(dataManager, profile.HomeWorldId);
		var dataCenter = GameDataResolver.ResolveDataCenterName(dataManager, profile.HomeWorldId);
		var title = GameDataResolver.ResolveTitleName(dataManager, profile.TitleId ?? 0, profile.Gender);
		return ProfileView.FormatPrimaryLine(profile.Name, title, world, dataCenter);
	}

	public static string FormatPrimaryLine(
		PlayerLocalData? data, LodestoneProfile? profile,
		string fallbackName, string fallbackWorld, string? fallbackDataCenter)
	{
		if (data != null)
		{
			var dataManager = Plugin.DataManager;
			var title = GameDataResolver.ResolveTitleName(dataManager, data.TitleId, data.Gender);
			var world = GameDataResolver.ResolveWorldName(dataManager, data.HomeWorldId);
			var dataCenter = GameDataResolver.ResolveDataCenterName(dataManager, data.HomeWorldId);
			return ProfileView.FormatPrimaryLine(data.Name, title, world, dataCenter);
		}

		return profile != null
			? ProfileView.FormatPrimaryLine(profile)
			: ProfileView.FormatPrimaryLine(fallbackName, null, fallbackWorld, fallbackDataCenter);
	}

	public static void DrawField(string label, string value, bool isDrift = false)
	{
		ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{label}:");
		ImGui.SameLine();
		if (isDrift)
		{
			ImGui.TextColored(ImGuiColors.WarningForeground, value);
		}
		else
		{
			ImGui.TextUnformatted(value);
		}
	}

	public static bool NameDrifted(PlayerLocalData data, LodestoneProfile profile) =>
		data.Name != profile.Name;

	public static bool HomeWorldDrifted(PlayerLocalData data, LodestoneProfile profile) =>
		data.HomeWorldId != profile.HomeWorldId;

	public static bool JobDrifted(PlayerLocalData data, LodestoneProfile profile) =>
		profile.JobId is { } jobId && (jobId != data.JobId || profile.Level != data.Level);

	public static void DrawCollapsedBody(
		LodestoneProfile profile, PlayerLocalData? compareData = null,
		Lazy<IReadOnlyList<NameHistoryEntry>>? nameHistory = null, DateTime? fetchedAtUtc = null)
	{
		ProfileView.DrawIdentityFields(profile, compareData, fetchedAtUtc);
		ProfileView.DrawTransfers(profile, nameHistory);

		if (profile.IsPartiallyPrivate)
		{
			ProfileView.DrawPrivateNotice(profile, compareData);
			return;
		}

		ProfileView.DrawCoreFields(profile, compareData);
	}

	private static void DrawTransfers(
		LodestoneProfile profile, Lazy<IReadOnlyList<NameHistoryEntry>>? nameHistory)
	{
		if (nameHistory == null)
		{
			return;
		}

		var currentWorld = GameDataResolver.ResolveWorldName(Plugin.DataManager, profile.HomeWorldId);
		var worlds = nameHistory.Value
			.Select(entry => entry.HomeWorldName)
			.Append(currentWorld)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(world => world, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (worlds.Length > 1)
		{
			ProfileView.DrawField("Transfers", string.Join(", ", worlds));
		}
	}

	private static void DrawIdentityFields(
		LodestoneProfile profile, PlayerLocalData? compareData, DateTime? fetchedAtUtc = null)
	{
		var world = GameDataResolver.ResolveWorldName(Plugin.DataManager, profile.HomeWorldId);
		ProfileView.DrawField("Lodestone ID", profile.LodestoneId.ToString());
		ProfileView.DrawField(
			"Name", profile.Name, compareData != null && ProfileView.NameDrifted(compareData, profile));
		ProfileView.DrawField(
			"Home World", world,
			compareData != null && ProfileView.HomeWorldDrifted(compareData, profile));

		if (fetchedAtUtc is { } timestamp)
		{
			ProfileView.DrawField("Time found", DisplayFormat.Timestamp(timestamp));
		}
	}

	public static string? FormatJobLine(string? jobAbbreviation, string? levelText) =>
		jobAbbreviation switch
		{
			null => null,
			var job when levelText != null => $"{job} (Lv. {levelText})",
			var job => job,
		};

	public static string? FormatJobLine(LodestoneProfile profile)
	{
		var jobAbbreviation = profile.JobId is { } jobId
			? GameDataResolver.ResolveJobAbbreviation(Plugin.DataManager, jobId)
			: null;
		return ProfileView.FormatJobLine(jobAbbreviation, profile.Level?.ToString());
	}

	public static string? FormatJobLine(
		PlayerLocalData? data, LodestoneProfile? profile,
		string? fallbackJobAbbreviation, string? fallbackLevelText)
	{
		if (data != null)
		{
			var jobAbbreviation = GameDataResolver.ResolveJobAbbreviation(Plugin.DataManager, data.JobId);
			return ProfileView.FormatJobLine(jobAbbreviation, data.Level.ToString());
		}

		return profile != null
			? ProfileView.FormatJobLine(profile)
			: ProfileView.FormatJobLine(fallbackJobAbbreviation, fallbackLevelText);
	}

	private static void DrawCoreFields(LodestoneProfile profile, PlayerLocalData? compareData)
	{
		if (ProfileView.FormatJobLine(profile) is { } jobLine)
		{
			ProfileView.DrawField(
				"Current Job", jobLine,
				compareData != null && ProfileView.JobDrifted(compareData, profile));
		}

		if (profile.FreeCompanyName != null)
		{
			ProfileView.DrawField("Free Company", profile.FreeCompanyName);
			ProfileView.DrawField("Free Company ID", profile.FreeCompanyId?.ToString() ?? "unknown");
		}

	}

	private static void DrawPrivateNotice(LodestoneProfile profile, PlayerLocalData? compareData)
	{
		ImGui.TextColored(ImGuiColors.ErrorForeground, "This character's profile is private.");
		if (ProfileView.FormatJobLine(profile) is { } jobLine)
		{
			ProfileView.DrawField(
				"Current Job", jobLine,
				compareData != null && ProfileView.JobDrifted(compareData, profile));
		}
	}
}
