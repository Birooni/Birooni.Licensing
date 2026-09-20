[Setup]
AppName=Family Loader
AppVersion=1.1.0
AppPublisher=Birooni Tools
DefaultDirName={userappdata}\Autodesk\Revit\Addins
DefaultGroupName=Family Loader
DisableProgramGroupPage=yes
OutputBaseFilename=FamilyLoader_Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
OutputDir=Output

[Types]
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "revit2020"; Description: "Install for Revit 2020"; Types: custom
Name: "revit2021"; Description: "Install for Revit 2021"; Types: custom
Name: "revit2022"; Description: "Install for Revit 2022"; Types: custom
Name: "revit2023"; Description: "Install for Revit 2023"; Types: custom
Name: "revit2024"; Description: "Install for Revit 2024"; Types: custom
Name: "revit2025"; Description: "Install for Revit 2025"; Types: custom
Name: "revit2026"; Description: "Install for Revit 2026"; Types: custom
Name: "revit2027"; Description: "Install for Revit 2027"; Types: custom

[Files]
; Revit 2020
Source: "2023\FamilyLoader.addin"; DestDir: "{app}\2020"; Components: revit2020; Flags: ignoreversion
Source: "2023\bin\x64\Debug\*"; DestDir: "{app}\2020\Family Loader"; Components: revit2020; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2021
Source: "2023\FamilyLoader.addin"; DestDir: "{app}\2021"; Components: revit2021; Flags: ignoreversion
Source: "2023\bin\x64\Debug\*"; DestDir: "{app}\2021\Family Loader"; Components: revit2021; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2022
Source: "2023\FamilyLoader.addin"; DestDir: "{app}\2022"; Components: revit2022; Flags: ignoreversion
Source: "2023\bin\x64\Debug\*"; DestDir: "{app}\2022\Family Loader"; Components: revit2022; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2023
Source: "2023\FamilyLoader.addin"; DestDir: "{app}\2023"; Components: revit2023; Flags: ignoreversion
Source: "2023\bin\x64\Debug\*"; DestDir: "{app}\2023\Family Loader"; Components: revit2023; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2024
Source: "2023\FamilyLoader.addin"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "2023\bin\x64\Debug\*"; DestDir: "{app}\2024\Family Loader"; Components: revit2024; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2025
Source: "2025\FamilyLoader.addin"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "2025\bin\x64\Debug\*"; DestDir: "{app}\2025\Family Loader"; Components: revit2025; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2026
Source: "2025\FamilyLoader.addin"; DestDir: "{app}\2026"; Components: revit2026; Flags: ignoreversion
Source: "2025\bin\x64\Debug\*"; DestDir: "{app}\2026\Family Loader"; Components: revit2026; Flags: ignoreversion recursesubdirs createallsubdirs

; Revit 2027
Source: "2025\FamilyLoader.addin"; DestDir: "{app}\2027"; Components: revit2027; Flags: ignoreversion
Source: "2025\bin\x64\Debug\*"; DestDir: "{app}\2027\Family Loader"; Components: revit2027; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Optional: If you want an uninstaller in the Start Menu
Name: "{group}\Uninstall Family Loader"; Filename: "{uninstallexe}"

[Code]
// You can add custom logic here if needed.
