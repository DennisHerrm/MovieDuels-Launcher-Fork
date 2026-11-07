# MovieDuels Launcher

The launcher for [Star Wars: Movie Duels](https://www.moddb.com/mods/movie-duels): one window to start the
singleplayer or the multiplayer game, read the team's news, find out about the mod and set a few options.

It is a WPF application for .NET Framework 4.8, which is part of Windows 10 and 11, so players need to
install nothing. It builds to a single file, `MovieDuels-Launcher.exe`, with all art and sound inside it.

## Using it

Put `MovieDuels-Launcher.exe` in the MovieDuels game folder, next to `MovieDuels-SP.x86_64.exe` and
`MovieDuels-MP.x86_64.exe`.

The top bars hold the pages:

| Page | What it has |
| --- | --- |
| PLAY | the art, SINGLEPLAYER and MULTIPLAYER either side of the emblem |
| NEWS | the team's articles, side by side: drag them, use the mouse wheel or the arrows; a click opens one |
| ABOUT | links (ModDB, Nexus Mods, Discord, YouTube, JKHub), About (the mod), FAQ, Team, and all Credits (searchable) |
| SETTINGS | Renderer, Display, Resolution, Controller; the launcher's Music and Check for updates; the installation and add-ons; Open folder (the game folder) and Open config folder (`Documents\My Games\MovieDuels\MD`) |

| Button | What it does |
| --- | --- |
| SINGLEPLAYER | starts `MovieDuels-SP.x86_64.exe` and closes the launcher |
| MULTIPLAYER | opens the multiplayer card: Start multiplayer (closes the launcher) or Start server (the FFA server in its own window; the launcher stays open). Update 9 and later; before that it says multiplayer is coming soon |
| Emblem | red: a new update is out (click: what's new, and buttons to ModDB and Nexus Mods). Otherwise a click looks for an update |
| Icons top left | open the mod's pages in the browser |
| Speaker, –, × | music on / off, minimise, close (Esc goes back a page, and closes on PLAY) |

The game options are passed to the game as `+set` values (`cl_renderer`, `com_rend2`, `r_fullscreen`, `r_mode`,
`in_joystick`) and written into the game's own configs. When the launcher opens, it reads them back from the
config the game wrote last (`MovieDuels-SP.cfg` or `MovieDuels-MP.cfg`), so a change made in the game stays; without
a config (a new install) they start as Vanilla, windowed, desktop resolution and no controller; the launcher's options are remembered in
`Documents\My Games\MovieDuels\MovieDuels-Launcher.ini`. The animation style (`g_ActivateAnimationStyle`,
Update 9) is set in the game's own menu.

The launcher reads which MovieDuels update the game's assets are the way the game does: the singleplayer
main menu (`ui/main.menu`, from the last pk3 of the `MD` folder that has one) sets it with
`uiScript "mdUpdate" "9"`; without that line it is Update 8. The update is shown at the bottom left of the
window, and multiplayer is only started on Update 9 and later.

### Updates

The launcher downloads nothing. When it opens it reads `latest.json` from the public repository
[MovieDuels-Launcher-News](https://github.com/MovieDuels/MovieDuels-Launcher-News); when the
update there is newer than the installed one, the emblem glows red, a reminder shows above it and PLAY gets a
dot on the other pages. The emblem's window lists what's new and opens ModDB or Nexus Mods, where players get
the update as before. Check for updates (SETTINGS) turns this off.

When the launcher then finds a newer update installed than the last time it ran, it checks the new files once:
the bars either side of the emblem fill up, and SINGLEPLAYER and MULTIPLAYER come back when they are full. The
check (also SETTINGS > Check installation) looks for Jedi Academy's `base\assets0-3.pk3` and opens every pk3 of
the `MD` folder; it changes nothing. Under ADDONS, SETTINGS shows whether the Jedi Outcast add-on
(`MD\r_outcastaddon.pk3` and `base\0-assets0, 1, 2, 5.pk3`) and TOTG S1: Dark Forces 2 (`MD\TOTG_DF2.pk3`) are
installed, with a link to their ModDB page (`FilesCheck.Addons`).

### News

`news.json` in MovieDuels-Launcher-News lists the articles, newest first; each one is a small HTML file and a
picture in its `news` folder. The NEWS button flashes while there are articles a player hasn't seen. What was read is kept in
`Documents\My Games\MovieDuels\LauncherNews`, so the news also show offline; a file read less than 5 minutes
ago comes from there, so opening the launcher again and again doesn't ask GitHub each time. The news repository's README
explains the files; the address is `ContentUrl` in `MovieDuels-Launcher\Online.cs`.
A YouTube embed (`<iframe src="https://www.youtube.com/embed/...">`, from YouTube's Share > Embed) shows as the
video's picture with a play button and opens the video in the browser; the launcher has no browser engine to play
it inside.

The launcher's build is shown at the bottom right, from `MovieDuels-Launcher\AutoVersion.cs`
(like the game's `AutoVersion.h`); change it there for each release.

## Building

Open `MovieDuels-Launcher.sln` in Visual Studio (with the ".NET desktop development" workload) and build,
or from a Developer Command Prompt:

```
msbuild MovieDuels-Launcher.sln -p:Configuration=Release
```

The exe is written to `MovieDuels-Launcher\bin\Release`.

## Changing it

- **Art and sound**: `MovieDuels-Launcher\Assets`. The images are the design's 1920x1080 layers, stacked in
  `MainWindow.xaml`; replace a file (same name) and rebuild. `Download Button.png` and
  `Download Ready Button.png` are the red and teal emblem.
- **Texts of ABOUT**: `MovieDuels-Launcher\Assets\Text` (`About.txt`, `FAQ.txt`, `Team.txt`); the format is
  explained at the top of each file. The credits come from the game's own `MD\ReadMe\ReadMe_Credits.txt`.
- **News and updates**: the MovieDuels-Launcher-News repository; no new launcher needed.
- **Layout, colours, link addresses**: `MainWindow.xaml`. Each link button's address is its `Tag`.
- **Game exe names, command line, options**: `MainWindow.xaml.cs` and `LauncherSettings.cs`.
- The design's Background Panel is glass (81% opaque). The window is made solid by the black `Border`
  under it in `MainWindow.xaml`; remove that one element for a see-through window.
