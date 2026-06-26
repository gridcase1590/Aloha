# Aloha
### Tropical Browser
Warning: This is a freedom oriented browser. Not a privacy one.
A Win9x-flavored shell built
around the Chromium engine (Microsoft Edge WebView2). - for now.

Inspired by DarkFantasy
## Requirements

- Windows (x64)
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
  (already present on most Windows 10/11 installs)

## Build

Open `Aloha.csproj` in Visual Studio (2022 or newer) and build for **x64**, or:

```
dotnet build Aloha.csproj -c Release
```

NuGet restores the two dependencies (WebView2 and Titanium.Web.Proxy) automatically.

## License

[Apache 2.0](LICENSE.txt). Third-party attribution lives in [NOTICE.txt](NOTICE.txt).
