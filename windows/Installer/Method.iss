; Copyright (C) 2026  Kovalyov Georgiy
; This file is part of Method.
;
; Method is free software: you can redistribute it and/or modify it under the
; terms of the GNU General Public License as published by the Free Software
; Foundation, either version 3 of the License, or (at your option) any later
; version. See LICENSE for details.
;
; Установщик Windows-клиента. Собирается скриптом Scripts\build-installer.ps1;
; вручную — iscc Method.iss /DSource="..\publish\win-x64" /DVersion=0.1.0

#ifndef Source
  #define Source "..\publish\win-x64"
#endif

#ifndef Version
  #define Version "0.1.0"
#endif

#define Name "Method"
#define Publisher "Kovalyov Georgiy"
#define ExeName "Method.exe"
#define IconFile "..\Method.App\Assets\app.ico"

[Setup]
; Опознаватель менять нельзя никогда: по нему Windows находит прошлую установку
; и понимает, что это обновление, а не второй продукт с тем же именем.
AppId={{7E4B2A91-3C6D-4F58-9A0E-5D2B8C17F3A4}
AppName={#Name}
AppVersion={#Version}
AppVerName={#Name} {#Version}
AppPublisher={#Publisher}
AppCopyright=Copyright (C) 2026 Kovalyov Georgiy. GNU GPL v3 or later.
AppSupportURL=https://github.com/lstcrsdd/method-network-client
AppUpdatesURL=https://github.com/lstcrsdd/method-network-client/releases
VersionInfoVersion={#Version}
VersionInfoProductName={#Name}

; Каталог выбирается, как и положено. Страница была выключена, пока клиент
; ставил себя сам: путь %ProgramFiles%\Method был зашит в нём, и выбор другого
; каталога развёл бы файлы по двум местам. Теперь мастер называет свой каталог
; прямо (--dir ниже), клиент ставится туда, куда указал человек, а умолчание
; осталось прежним.
DefaultDirName={commonpf}\{#Name}
AppendDefaultDirName=no
DisableProgramGroupPage=yes
DefaultGroupName={#Name}
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#Name}

OutputDir=Output
OutputBaseFilename=Method-Setup-{#Version}
SetupIconFile={#IconFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Служба и правила брандмауэра ставятся только с правами администратора.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 1809 — нижняя граница: раньше нет ни нужных вызовов WFP, ни
; поведения службы, на которое клиент рассчитывает.
MinVersion=10.0.17763

; Клиент держит именованный канал и файл службы; мастер обязан его закрыть,
; иначе занятый файл молча не заменится и «обновление» оставит прежнюю сборку.
CloseApplications=force
RestartApplications=no

; GPL требует передавать текст лицензии вместе с программой. Показываем его в
; мастере и кладём рядом с исполняемым файлом.
LicenseFile=..\..\LICENSE

; --- Подпись кода -----------------------------------------------------------
; Без подписи SmartScreen пугает при первом запуске, и тем сильнее, что рядом
; ставится сетевой драйвер Wintun. Получите сертификат, настройте в Inno
; Tools -> Configure Sign Tools средство с именем signtool и снимите
; комментарий с двух строк:
; SignTool=signtool
; SignedUninstaller=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: checkedonce

[InstallDelete]
; Прежние сборки раскладывались россыпью: сотни библиотек .NET рядом с exe.
; Нынешняя — один самодостаточный файл, и соседи ему не нужны. Оставить их —
; значит держать в каталоге двести мегабайт мусора и сбивать с толку того, кто
; туда заглянет: рядом с новым Method.exe лежал бы Method.dll позапрошлой
; версии. Каталог наш, других владельцев у него нет; собственный деинсталлятор
; Inno (unins000.*) под маску не попадает намеренно.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\createdump.exe"

[Files]
; Один самодостаточный файл: движок sing-box и wintun.dll вшиты в него и
; раскладываются при установке.
Source: "{#Source}\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Библиотека общего ядра — рядом, а не внутри: это нативный код, в один файл
; .NET его не убирает. Без неё клиент работает, но выбор узла ведёт сам движок,
; а не ядро, поэтому отсутствие файла — повод остановить сборку установщика, и
; проверка стоит в build-installer.ps1.
Source: "{#Source}\method_core_ffi.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Обязательства GPL: текст лицензии и сведения о сторонних компонентах едут
; вместе со сборкой, потому что обязательство возникает из самой раздачи.
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\COPYRIGHT"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#Name}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#Name}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
; Привилегированную часть ставит сам клиент: он один знает, как назвать службу,
; какие права урезать у рабочего каталога и какие правила брандмауэра нужны
; движку. Повторять это в мастере значило бы завести второе описание того же,
; которое разойдётся с первым при первой же правке.
Filename: "{app}\{#ExeName}"; Parameters: "--install --dir ""{app}"""; StatusMsg: "Регистрация службы туннеля…"; Flags: runhidden waituntilterminated
Filename: "{app}\{#ExeName}"; Description: "Запустить {#Name}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Снять службу, правила брандмауэра и блокировку исходящего трафика. Делает это
; тот же клиент и точечно — по своим путям: чужой VPN на машине не трогаем.
Filename: "{app}\{#ExeName}"; Parameters: "--uninstall --dir ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "MethodUninstall"

[Code]
// Остановка клиента ДО копирования файлов.
//
// Служба работает под SYSTEM из того же Method.exe и держит файл открытым.
// Занятый файл мастер заменить не может и пропускает его молча — установка при
// этом рапортует об успехе, а на машине остаётся прежняя сборка. Секция [Run]
// для этого не годится: она выполняется уже после копирования.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';

  // По-хорошему: остановленная служба не считается упавшей, и система не
  // поднимет её обратно через пять секунд посреди замены файлов.
  Exec('sc.exe', 'stop MethodTunnel', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);

  // Гасим только свой образ. Никаких общих taskkill по имени sing-box: под ним
  // на машине может работать чужой клиент, и он тут ни при чём.
  Exec('taskkill.exe', '/F /IM Method.exe /T', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1000);
end;

// Проверка, что замена действительно состоялась. Молчаливый пропуск занятого
// файла — худший исход: человек уверен, что обновился, а исправления не
// приехали, и следующий отчёт об ошибке будет про уже починенное.
procedure VerifyVersion();
var
  Installed: String;
begin
  if not GetVersionNumbersString(ExpandConstant('{app}\{#ExeName}'), Installed) then
    Exit;

  if Pos('{#Version}', Installed) <> 1 then
    MsgBox('Внимание: на диске осталась версия ' + Installed + ' вместо {#Version}.' + #13#10 +
           'Файл был занят и не заменился. Закройте Method и запустите установку заново.',
           mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    VerifyVersion();
end;
