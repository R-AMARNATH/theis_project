# load_azure_env.ps1
# Fetches each Azure storage account's connection string (accounts already
# exist -- this just re-reads them) and sets them as env vars in the CURRENT
# PowerShell session, so stage_dataset.py's os.environ.get(...) calls find
# them. Only lasts for this session -- re-run if you open a new terminal.
#
# Usage: . .\load_azure_env.ps1   (note the leading ". " -- dot-source it so
# the env vars persist in your current shell, not a child process)

$manifest = Get-Content ..\files\manifest.json | ConvertFrom-Json
$azureEntries = $manifest | Where-Object { $_.cloud -eq 'azure' }

foreach ($entry in $azureEntries) {
    $account = $entry.account
    $connStr = az storage account show-connection-string --name $account --query connectionString -o tsv 2>$null
    if ($connStr) {
        $varName = "AZURE_STORAGE_CONNECTION_STRING_$($account.ToUpper())"
        Set-Item -Path "env:$varName" -Value $connStr
        Write-Host "[OK]   $account -> `$env:$varName set"
    } else {
        Write-Host "[FAIL] $account -> could not fetch connection string" -ForegroundColor Red
    }
}

Write-Host "`nDone. $($azureEntries.Count) accounts processed for this session."
