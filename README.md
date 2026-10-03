# Velivo

Lekka, prywatna przeglądarka dla Windows (WPF + WebView2) z wbudowanym dodatkiem **Szybki Dostęp**.

## Struktura repozytorium

| Folder | Zawartość |
|---|---|
| `src/` | Kod źródłowy programu (C#) |
| `src/QuickAccessExtension/` | Dodatek Szybki Dostęp (strona nowej karty, ikonka Sejfu w polach logowania) |
| `src/installer.iss` | Skrypt instalatora (Inno Setup) |
| `Instalator/` | Gotowy instalator `Velivo-Setup-1.22.exe` |
| `.github/workflows/instalator.yml` | Automatyczna budowa instalatora |

## Budowanie

Każda zmiana w `src/` wysłana na GitHub automatycznie buduje nowy instalator (GitHub Actions, Windows)
i nadpisuje plik `Instalator/Velivo-Setup-1.22.exe`.

Ręcznie (Windows, .NET 10 SDK, Inno Setup 6):

```
dotnet publish src\Przegladarka.csproj -c Release -r win-x64 --self-contained false -o build\velivo
"%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" src\installer.iss
```

Foldery `build/`, `src/bin/` i `src/obj/` powstają przy kompilacji i nie są przechowywane w repozytorium.
