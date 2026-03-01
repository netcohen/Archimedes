cd "C:\Users\netanel\Desktop\Archimedes\core"
$out = dotnet build --configuration Release 2>&1
$out | Select-String "error CS|Build succeeded|Build FAILED|Error\(s\)"
