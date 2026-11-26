[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'Nphies'
$manifestDirectory = Join-Path $root 'knowledge-sources'
$output = Join-Path $manifestDirectory 'all-references.csv'
$metadata = @{}
foreach ($entry in Import-Csv (Join-Path $manifestDirectory 'manifest.csv')) {
	$path = [IO.Path]::GetFullPath((Join-Path $manifestDirectory $entry.path))
	$metadata[$path] = $entry
}

$supported = @('.txt', '.md', '.markdown', '.csv', '.json', '.pdf', '.xlsx', '.docx', '.html', '.htm')
$excluded = @{}
$entries = New-Object 'System.Collections.Generic.List[object]'
function Visit-Directory([string] $directory) {
	foreach ($item in Get-ChildItem -LiteralPath $directory -Force | Sort-Object Name) {
		$reason = $null
		if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $reason = 'symbolic link or junction' }
		elseif ($item.PSIsContainer -and ($item.Name -in @('claims-request-reponses', 'claims-request-responses', 'licensed', 'archives') -or $item.Name -like 'DRG Release JSON Samples*')) { $reason = 'excluded payload/licensed/archive folder' }
		if ($reason) {
			Write-Warning "Excluded $($item.FullName.Substring($root.Length + 1)): $reason"
			continue
		}
		if ($item.PSIsContainer) { Visit-Directory $item.FullName; continue }
		if ($item.FullName -in @($output, (Join-Path $manifestDirectory 'manifest.csv'), (Join-Path $manifestDirectory 'starter.csv'))) { continue }
		if ($item.Extension.ToLowerInvariant() -notin $supported) {
			$extension = $item.Extension.ToLowerInvariant()
			if (!$excluded.ContainsKey($extension)) { $excluded[$extension] = 0 }
			$excluded[$extension]++
			continue
		}
		if ($metadata.ContainsKey($item.FullName)) { $entries.Add($metadata[$item.FullName]); continue }
		$relative = $item.FullName.Substring($root.Length + 1)
		$category = 'NphiesGuidance'
		$payer = ''
		if ($relative -match '^(architecture|integration|rules)\\' -or $relative -match '^knowledge-sources\\internal\\' -or $item.Name -in @('README.md', 'TO-DOWNLOAD.md', 'inventory.json', 'applicability-review.json')) { $category = 'InternalGuidance' }
		elseif ($relative -match '^knowledge-sources\\regulator\\') { $category = 'RegulatorPolicy' }
		elseif ($relative -match '^knowledge-sources\\payers\\([^\\]+)\\') { $category = 'PayerPolicy'; $payer = $Matches[1] }
		$entries.Add([pscustomobject][ordered]@{
			path = '..\' + $relative
			category = $category
			payer = $payer
			title = [IO.Path]::GetFileNameWithoutExtension($item.Name)
			publisher_version = ''
			effective_from = ''
			effective_to = ''
			notes = 'Discovered recursively; applicability and publication dates are not inferred.'
		})
	}
}
Visit-Directory $root
$entries | Sort-Object path | Export-Csv -LiteralPath $output -NoTypeInformation -Encoding UTF8
Write-Output "Generated all-references.csv with $($entries.Count) reference sources. No database or embedding calls were made."
foreach ($extension in $excluded.Keys | Sort-Object) { Write-Warning "Unsupported extension ${extension}: $($excluded[$extension]) file(s) excluded" }
