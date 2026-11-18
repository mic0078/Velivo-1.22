; 🧩 wspólny .NET 10 dla wszystkich programów Velivo: programy bez wbudowanego .NET (kilkanaście MB zamiast ok. 150 MB każdy),
; instalator sprawdza .NET 10 Desktop Runtime i - gdy go brak - raz pobiera go od Microsoftu (ok. 55 MB). Dołączane do instalatorów Velivo.
[CustomMessages]
pl.GetDotnet=Pobieranie i instalacja .NET 10 Desktop Runtime od Microsoftu (jednorazowo, ok. 55 MB, wspólny dla wszystkich programów Velivo)…
en.GetDotnet=Downloading and installing .NET 10 Desktop Runtime from Microsoft (one time, about 55 MB, shared by all Velivo programs)…
pl.NeedDotnet=Nie udało się zainstalować .NET 10 Desktop Runtime (x64) – jest potrzebny do działania programu.%nSprawdź połączenie z internetem i uruchom instalator ponownie (albo zainstaluj go ze strony dotnet.microsoft.com).
en.NeedDotnet=Could not install .NET 10 Desktop Runtime (x64) – the program needs it.%nCheck the internet connection and run the installer again (or install it from dotnet.microsoft.com).

[Code]
function HasDesktopRuntime10: Boolean;
var FindRec: TFindRec;
begin
  // potrzebne OBA skladniki: Microsoft.NETCore.App i Microsoft.WindowsDesktop.App w wersji 10
  // (sam WindowsDesktop bez NETCore konczyl sie bledem "You must install or update .NET")
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    FindClose(FindRec);
    if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\10.*'), FindRec) then
    begin
      Result := True;
      FindClose(FindRec);
    end;
  end;
end;

// '' = jest albo właśnie zainstalowany; inaczej komunikat dla PrepareToInstall
function InstallDotnet10: String;
var Code: Integer; F: String;
begin
  Result := '';
  if HasDesktopRuntime10 then exit;
  WizardForm.StatusLabel.Caption := CustomMessage('GetDotnet');
  try
    DownloadTemporaryFile('https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe', 'dotnet-desktop.exe', '', nil);
    F := ExpandConstant('{tmp}\dotnet-desktop.exe');
    if not ShellExec('', F, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) or not HasDesktopRuntime10 then
      Result := CustomMessage('NeedDotnet');
  except
    Result := CustomMessage('NeedDotnet');
  end;
end;
