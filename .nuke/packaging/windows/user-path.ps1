# Adds or removes a folder from the user PATH (HKCU\Environment).
# Runs from the NSIS installer because NSIS strings are capped at 1024 characters, which truncates long PATH values.
param(
    [Parameter(Mandatory)][ValidateSet('add', 'remove')][string]$Action,
    [Parameter(Mandatory)][string]$Folder
)

$ErrorActionPreference = 'Stop'
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
try {
    # Read unexpanded so entries such as %USERPROFILE%\bin survive the rewrite.
    $current = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $entries = @($current.Split(';') | Where-Object { $_ -ne '' })
    $target = $Folder.TrimEnd('\')
    $others = @($entries | Where-Object { $_.TrimEnd('\') -ne $target })
    $present = $others.Count -ne $entries.Count

    if ($Action -eq 'add') {
        if ($present) { exit 0 }
        $value = if ($current.Trim(';') -eq '') { $Folder } else { $current.TrimEnd(';') + ';' + $Folder }
        $key.SetValue('Path', $value, [Microsoft.Win32.RegistryValueKind]::ExpandString)
    }
    elseif ($present) {
        if ($others.Count -eq 0) { $key.DeleteValue('Path') }
        else { $key.SetValue('Path', ($others -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString) }
    }
}
finally {
    $key.Dispose()
}
