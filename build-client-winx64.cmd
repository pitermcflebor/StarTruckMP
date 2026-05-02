dotnet publish StarTruckMP.Client/StarTruckMP.Client.csproj ^
    -c Debug ^
    -r win-x64 ^
    -p:EnableWindowsTargeting=true ^
    -o "%StarTruckBepInEx%/plugins/StarTruckMP"

dotnet publish StarTruckMP.Client/StarTruckMP.Client.csproj ^
    -c Debug ^
    -r win-x64 ^
    -p:EnableWindowsTargeting=true ^
    -o "%StarTruckBepInExSteam%/plugins/StarTruckMP"