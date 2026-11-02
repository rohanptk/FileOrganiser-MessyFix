# Downloads Organiser

A Windows tray app that moves the loose files in your Downloads folder into the folders you already built. It learns your folders from what's already in them, so it's useful from the first run.

## Use
1. Build: `dotnet publish src/App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish`
2. Run `publish\DownloadsOrganiser.exe`. A window lists every loose file with a suggested folder.
3. Per file: **Enter** moves it, **1–3** picks a suggestion, **F** browses to a folder, **A** always sends files like this to that folder, **S** skips, **Del** sends it to the Recycle Bin. Double-click opens the file.
4. Closing the window keeps the app in the tray, watching for new downloads. Switch on *Auto-move* to file confident matches automatically. Click the notification to undo a move.
5. *Archive a term…* moves a finished term folder into a sibling `_ARCHIVE` folder.

Dry run that moves nothing: `dotnet run --project src/Cli`

## Safety
- It only touches loose files at the top level of Downloads. It never moves folders and never overwrites a file: on a name clash the new file gets ` (2)`, and identical copies are flagged for you to decide.
- Nothing is deleted except to the Recycle Bin, and only when you ask.
- Every move is logged and can be undone.
- Memory, rules and settings live in `%LOCALAPPDATA%\DownloadsOrganiser\memory.json`, which you can edit in Notepad. `IgnoreWords` lists words like your name that shouldn't decide a folder. `Excludes` lists folders that should never be suggested.
