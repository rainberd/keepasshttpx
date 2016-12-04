# KeePassHttpX

This is fork of KeePassHttp plugin adjusted to support Wine on macOS and Linux.

# Installation

## KeePass (macOS)

1. Download latest KeePass 2.x for Windows from the official [website](http://keepass.info/).
2. Download latest Wineskin from [here](http://wineskin.urgesoftware.com/).
3. Create a new wrapper with Wine engine 1.7.52 (I tested this version but newer ones may work as well). Say `No` for Gecko and Mono installation in Wineskin wizzard.
4. Open created wrapper settings and go to `Screen Options`. Mark `Use Mac Driver instead of X11`. Click `Done`.
5. Next go to `Tools` tab and click on `Winetricks`. Install `msxml3` and `dotnet40` tricks. Close this window.
6. Click on `Install Software` and install KeePass.
7. Done! Now you have properly installed KeePass on Wine that runs nice and smoothly with no crappy Mono and X11 emulation.

## KeePassHttpX (macOS)
1. Download [KeePassHttpX](https://raw.github.com/sazonov/keepasshttpx/master/release/KeePassHttpX.plgx).
2. Open KeePass wrapper contents in Finder and go to `drive_c/Program Files/KeePass Password Safe 2/Plugins`.
3. Copy `KeePassHttpX.plgx` to `Plugins` directory.
4. Run KeePass.
5. Wine has some issues with tray icons and popup notifications so we need to disable questions on access to new db entries via KeePassHttp. Go to `Tools -> KeePassHttp Options` and set `Always allow access to entries` on `Advanced` tab.
