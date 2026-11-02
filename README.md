# Space Finder

Shows which folders and files are eating your drive, so you know what to clear before the next game update.

It only reads. It never deletes or changes anything.

## Download

Get `SpaceFinder.exe` from the [latest release](https://github.com/lmgoffical/space-finder/releases/latest) and open it. No install needed. Windows 10 & 11.

Windows may show "Windows protected your PC" because the app isn't signed. Click **More info → Run anyway**.

## Use it

- Pick a drive and press **Scan drive**, or **Pick folder…** to scan one folder.
- Double-click a folder to look inside. Backspace or **▲ Up** goes back.
- Right-click anything to open it in File Explorer or copy its path.

## Build it yourself

It's one C# file. With the .NET Framework compiler that comes with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /codepage:65001 /optimize+ /target:winexe /out:SpaceFinder.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SpaceFinder.cs
```
