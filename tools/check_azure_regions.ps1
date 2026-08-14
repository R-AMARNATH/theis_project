$regions = @("eastus","eastus2","southeastasia","westeurope","westus2","centralus","northeurope","uksouth","southcentralus")
$priority = @("Standard_B2s","Standard_F2s_v2","Standard_F2s","Standard_F2","Standard_D2ls_v6","Standard_D2ls_v5","Standard_A2_v2","Standard_B2ms","Standard_DS1_v2")

$results = @()
foreach ($region in $regions) {
    $chosen = $null
    foreach ($size in $priority) {
        $reason = az vm list-skus --location $region --size $size --all --query "[0].restrictions[0].reasonCode" -o tsv
        if ([string]::IsNullOrEmpty($reason)) {
            $chosen = $size
            break
        }
    }
    if ($null -eq $chosen) { $chosen = "NONE AVAILABLE" }
    Write-Host "$region -> $chosen"
    $results += [PSCustomObject]@{ Region = $region; RecommendedSize = $chosen }
}
$results | Export-Csv -Path "azure_region_size_availability.csv" -NoTypeInformation
Write-Host "Saved to azure_region_size_availability.csv"
