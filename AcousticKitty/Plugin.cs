// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using AcousticKitty.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AcousticKitty;

public sealed class Plugin : IDalamudPlugin
{
	#region Dalamud Services

	[PluginService]
	internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
	[PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
	[PluginService] internal static IClientState ClientState { get; private set; } = null!;
	[PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
	[PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
	[PluginService] internal static IFramework Framework { get; private set; } = null!;
	[PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
	[PluginService] internal static IDataManager DataManager { get; private set; } = null!;
	[PluginService] internal static IPluginLog Log { get; private set; } = null!;

	#endregion

	#region Plugin Services

	private const string CommandName = "/lodestone";

	public Configuration Configuration { get; }

	public readonly WindowSystem WindowSystem = new("AcousticKitty");

	internal LodestoneClient LodestoneClient { get; }

	internal AvatarTextureCache AvatarTextureCache { get; }

	internal GroupSearchService GroupSearchService { get; }

	internal NearbyCharactersService NearbyCharactersService { get; }

	internal DatabaseOverviewService DatabaseOverviewService { get; }

	private readonly LodestoneCache lodestoneCache;
	private readonly CharacterDirectory characterDirectory;
	private readonly AvatarWorldHistoryService avatarWorldHistoryService;
	private readonly MainWindow mainWindow;

	#endregion

	#region Lifecycle

	public Plugin()
	{
		this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

		this.LodestoneClient = new LodestoneClient(this.Configuration, Log);
		this.lodestoneCache = new LodestoneCache(PluginInterface.ConfigDirectory.FullName);
		this.characterDirectory =
			new CharacterDirectory(PluginInterface.ConfigDirectory.FullName, Log);
		this.avatarWorldHistoryService = new AvatarWorldHistoryService(
			this.LodestoneClient, this.characterDirectory, DataManager, Log);
		this.AvatarTextureCache = new AvatarTextureCache(this.LodestoneClient, TextureProvider);
		this.GroupSearchService = new GroupSearchService(
			this.LodestoneClient,
			this.lodestoneCache,
			this.characterDirectory,
			DataManager,
			this.AvatarTextureCache,
			Log);
		this.NearbyCharactersService = new NearbyCharactersService(
			PlayerState,
			ObjectTable,
			ClientState,
			Framework,
			DataManager,
			this.LodestoneClient,
			this.lodestoneCache,
			this.characterDirectory,
			this.Configuration,
			Log);
		this.DatabaseOverviewService = new DatabaseOverviewService(
			this.lodestoneCache, this.characterDirectory, DataManager, PlayerState, Log);

		this.mainWindow = new MainWindow(this);

		this.WindowSystem.AddWindow(this.mainWindow);

		CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
		{
			HelpMessage = "Opens the Acoustic Kitty window.",
		});

		PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
		PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
		PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;
	}

	public void Dispose()
	{
		PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
		PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
		PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;

		this.WindowSystem.RemoveAllWindows();

		this.mainWindow.Dispose();

		this.NearbyCharactersService.Dispose();
		this.GroupSearchService.Dispose();
		this.AvatarTextureCache.Dispose();
		this.LodestoneClient.Dispose();
		this.lodestoneCache.Dispose();
		this.characterDirectory.Dispose();

		CommandManager.RemoveHandler(CommandName);
	}

	#endregion

	#region Commands

	private void OnCommand(string command, string args) => this.mainWindow.Toggle();

	public void ToggleConfigUi()
	{
		this.mainWindow.SelectedTab = MainWindowTab.Settings;
		this.mainWindow.IsOpen = true;
	}

	public void ToggleMainUi() => this.mainWindow.Toggle();

	#endregion
}
