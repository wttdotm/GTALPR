<#
.SYNOPSIS
Checks every saved Flock surveillance scene manifest.

.DESCRIPTION
Auto-discovers the flat FlockSurveillance\Captures directory and the legacy
FlockSurveillance\Scenes directory under the Windows Pictures folder,
including OneDrive redirection. Validates JSON structure, entity references,
capture counters, completeness metadata, and performance.

.EXAMPLE
.\Check-SurveillanceScenes.ps1

.EXAMPLE
.\Check-SurveillanceScenes.ps1 -Since (Get-Date).Date

.EXAMPLE
.\Check-SurveillanceScenes.ps1 -WarnCaptureMilliseconds 100 -FailOnWarning
#>
[CmdletBinding()]
param(
    [string]$ScenePath,
    [datetime]$Since = [datetime]::MinValue,
    [double]$WarnCaptureMilliseconds = 50.0,
    [switch]$FailOnWarning
)

$ErrorActionPreference = "Stop"

function Test-IsSceneManifestName {
    param([string]$Name)

    return (
        -not [string]::IsNullOrWhiteSpace($Name) -and
        (
            $Name.EndsWith(
                ".json",
                [StringComparison]::OrdinalIgnoreCase
            ) -or
            $Name.EndsWith(
                ".json.gz",
                [StringComparison]::OrdinalIgnoreCase
            )
        )
    )
}

function Get-SceneManifestIdentity {
    param([string]$Path)

    $name = [IO.Path]::GetFileName($Path)

    if ($name.EndsWith(
        ".json.gz",
        [StringComparison]::OrdinalIgnoreCase
    )) {
        return $name.Substring(0, $name.Length - ".json.gz".Length)
    }

    return $name.Substring(0, $name.Length - ".json".Length)
}

function Get-SceneManifestFiles {
    param(
        [string[]]$Directories,
        [datetime]$ModifiedSince = [datetime]::MinValue,
        [switch]$IgnoreEnumerationErrors
    )

    $selected = @{}
    $enumerationErrorAction = if ($IgnoreEnumerationErrors) {
        "SilentlyContinue"
    }
    else {
        "Stop"
    }

    for ($priority = 0; $priority -lt $Directories.Count; $priority++) {
        $directory = $Directories[$priority]

        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            continue
        }

        Get-ChildItem `
            -LiteralPath $directory `
            -Filter "*.json*" `
            -Recurse `
            -File `
            -ErrorAction $enumerationErrorAction |
            Where-Object {
                $_.LastWriteTime -ge $ModifiedSince -and
                (Test-IsSceneManifestName $_.Name)
            } |
            Sort-Object FullName |
            ForEach-Object {
                $identity = Get-SceneManifestIdentity $_.FullName
                $existing = $selected[$identity]
                $isGzip = $_.Name.EndsWith(
                    ".json.gz",
                    [StringComparison]::OrdinalIgnoreCase
                )
                $replace =
                    $null -eq $existing -or
                    $priority -lt $existing.Priority -or
                    (
                        $priority -eq $existing.Priority -and
                        $isGzip -and
                        -not $existing.IsGzip
                    )

                if ($replace) {
                    $selected[$identity] = [pscustomobject]@{
                        File = $_
                        Priority = $priority
                        IsGzip = $isGzip
                    }
                }
            }
    }

    return @(
        $selected.Values |
        ForEach-Object { $_.File } |
        Sort-Object LastWriteTime, FullName
    )
}

function Read-SceneManifestText {
    param([IO.FileInfo]$File)

    $maximumBytes = [long](16 * 1024 * 1024)
    $fileStream = $null
    $gzipStream = $null
    $contents = $null
    $reader = $null

    try {
        $fileStream = [IO.File]::Open(
            $File.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        )

        $inputStream = $fileStream

        if ($File.Name.EndsWith(
            ".json.gz",
            [StringComparison]::OrdinalIgnoreCase
        )) {
            $gzipStream = [IO.Compression.GZipStream]::new(
                $fileStream,
                [IO.Compression.CompressionMode]::Decompress
            )
            $inputStream = $gzipStream
        }

        $contents = [IO.MemoryStream]::new()
        $buffer = [byte[]]::new(81920)

        while (($read = $inputStream.Read(
            $buffer,
            0,
            $buffer.Length
        )) -gt 0) {
            if ($contents.Length + $read -gt $maximumBytes) {
                throw (
                    "The decompressed scene manifest exceeds the " +
                    "16 MB safety limit."
                )
            }

            $contents.Write($buffer, 0, $read)
        }

        if ($contents.Length -eq 0) {
            throw "The scene manifest is empty."
        }

        $contents.Position = 0
        $reader = [IO.StreamReader]::new(
            $contents,
            [Text.Encoding]::UTF8,
            $true
        )

        return $reader.ReadToEnd()
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }

        if ($null -ne $gzipStream) {
            $gzipStream.Dispose()
        }

        if ($null -ne $fileStream) {
            $fileStream.Dispose()
        }

        if ($null -ne $contents) {
            $contents.Dispose()
        }
    }
}

function Add-CandidateRoot {
    param(
        [System.Collections.ArrayList]$Candidates,
        [string]$PicturesPath
    )

    if ([string]::IsNullOrWhiteSpace($PicturesPath)) {
        return
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($PicturesPath)
    $root = Join-Path $expanded "FlockSurveillance"

    foreach ($existing in $Candidates) {
        if ([string]::Equals(
            $existing,
            $root,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            return
        }
    }

    [void]$Candidates.Add($root)
}

function Get-ManifestDirectoriesForRoot {
    param([string]$Root)

    $directories = New-Object System.Collections.ArrayList

    foreach ($leaf in @("Captures", "Scenes")) {
        $path = Join-Path $Root $leaf

        if (Test-Path -LiteralPath $path -PathType Container) {
            [void]$directories.Add([IO.Path]::GetFullPath($path))
        }
    }

    return @($directories)
}

function Resolve-ScenePaths {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)

        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "Scene directory does not exist: $resolved"
        }

        $children = @(Get-ManifestDirectoriesForRoot $resolved)

        if ($children.Count -gt 0) {
            return $children
        }

        return @($resolved)
    }

    $candidates = New-Object System.Collections.ArrayList

    try {
        Add-CandidateRoot `
            -Candidates $candidates `
            -PicturesPath ([Environment]::GetFolderPath("MyPictures"))
    }
    catch {
        # Continue through the Windows and OneDrive fallbacks below.
    }

    try {
        $shellFolders = Get-ItemProperty `
            -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"

        foreach ($name in @(
            "My Pictures",
            "{0DDD015D-B06C-45D5-8C4C-F59713854639}"
        )) {
            $property = $shellFolders.PSObject.Properties[$name]

            if ($null -ne $property) {
                Add-CandidateRoot `
                    -Candidates $candidates `
                    -PicturesPath ([string]$property.Value)
            }
        }
    }
    catch {
        # Registry discovery is optional.
    }

    if (-not [string]::IsNullOrWhiteSpace($env:OneDrive)) {
        Add-CandidateRoot `
            -Candidates $candidates `
            -PicturesPath (Join-Path $env:OneDrive "Pictures")
    }

    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        Add-CandidateRoot `
            -Candidates $candidates `
            -PicturesPath (Join-Path $env:USERPROFILE "Pictures")

        Add-CandidateRoot `
            -Candidates $candidates `
            -PicturesPath (Join-Path $env:USERPROFILE "OneDrive\Pictures")
    }

    foreach ($candidate in $candidates) {
        $directories = @(Get-ManifestDirectoriesForRoot $candidate)

        if (
            $directories.Count -gt 0 -and
            $null -ne (Get-SceneManifestFiles `
                -Directories $directories `
                -IgnoreEnumerationErrors |
                Select-Object -First 1)
        ) {
            return $directories
        }
    }

    foreach ($candidate in $candidates) {
        $directories = @(Get-ManifestDirectoriesForRoot $candidate)

        if ($directories.Count -gt 0) {
            return $directories
        }
    }

    $searched = $candidates -join [Environment]::NewLine
    throw "Could not find the Flock capture directories. Searched:`n$searched`nPass one explicitly with -ScenePath."
}

function Add-Issue {
    param(
        [System.Collections.ArrayList]$IssueList,
        [ValidateSet("Error", "Warning")]
        [string]$Severity,
        [string]$File,
        [string]$Message
    )

    [void]$IssueList.Add(
        [pscustomobject]@{
            Severity = $Severity
            File = $File
            Message = $Message
        }
    )
}

function Test-HasProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    return $null -ne $Value -and
        $null -ne $Value.PSObject.Properties[$Name]
}

function Test-IsJsonNumber {
    param([object]$Value)

    return (
        $Value -is [byte] -or
        $Value -is [sbyte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64] -or
        $Value -is [single] -or
        $Value -is [double] -or
        $Value -is [decimal]
    )
}

function Test-IsJsonInteger {
    param([object]$Value)

    return (
        $Value -is [byte] -or
        $Value -is [sbyte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64]
    )
}

function Test-SceneVector {
    param(
        [object]$Value,
        [string]$Label,
        [System.Collections.ArrayList]$IssueList,
        [string]$File
    )

    if ($null -eq $Value) {
        Add-Issue $IssueList "Error" $File "$Label is missing."
        return $false
    }

    $valid = $true

    foreach ($component in @("X", "Y", "Z")) {
        $property = $Value.PSObject.Properties[$component]

        if ($null -eq $property -or -not (Test-IsJsonNumber $property.Value)) {
            Add-Issue `
                $IssueList `
                "Error" `
                $File `
                "$Label.$component must be a JSON number."
            $valid = $false
        }
    }

    return $valid
}

function Test-Reference {
    param(
        [hashtable]$Ids,
        [string]$EntityId,
        [bool]$Required,
        [string]$Label,
        [System.Collections.ArrayList]$IssueList,
        [string]$File
    )

    if ([string]::IsNullOrWhiteSpace($EntityId)) {
        if ($Required) {
            Add-Issue $IssueList "Error" $File "$Label is missing."
        }

        return
    }

    if (-not $Ids.ContainsKey($EntityId)) {
        Add-Issue `
            $IssueList `
            "Error" `
            $File `
            "$Label refers to missing entity '$EntityId'."
    }
}

function Find-NonFiniteNumbers {
    param(
        [object]$Value,
        [string]$JsonPath,
        [System.Collections.ArrayList]$Results,
        [int]$Depth = 0
    )

    if ($null -eq $Value -or $Depth -gt 128) {
        return
    }

    if ($Value -is [double]) {
        if ([double]::IsNaN($Value) -or [double]::IsInfinity($Value)) {
            [void]$Results.Add($JsonPath)
        }

        return
    }

    if ($Value -is [single]) {
        if ([single]::IsNaN($Value) -or [single]::IsInfinity($Value)) {
            [void]$Results.Add($JsonPath)
        }

        return
    }

    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            Find-NonFiniteNumbers `
                -Value $Value[$key] `
                -JsonPath "$JsonPath.$key" `
                -Results $Results `
                -Depth ($Depth + 1)
        }

        return
    }

    if (
        $Value -is [System.Collections.IEnumerable] -and
        -not ($Value -is [string])
    ) {
        $index = 0

        foreach ($item in $Value) {
            Find-NonFiniteNumbers `
                -Value $item `
                -JsonPath "$JsonPath[$index]" `
                -Results $Results `
                -Depth ($Depth + 1)
            $index++
        }

        return
    }

    if ($Value -is [pscustomobject]) {
        foreach ($property in $Value.PSObject.Properties) {
            Find-NonFiniteNumbers `
                -Value $property.Value `
                -JsonPath "$JsonPath.$($property.Name)" `
                -Results $Results `
                -Depth ($Depth + 1)
        }
    }
}

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return 0.0
    }

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    $index = [Math]::Max(0, [Math]::Min($index, $sorted.Count - 1))
    return [double]$sorted[$index]
}

function Get-ManifestDisplayName {
    param(
        [IO.FileInfo]$File,
        [string[]]$Directories
    )

    foreach ($directory in $Directories) {
        $prefix = $directory.TrimEnd("\") + "\"

        if ($File.FullName.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            $leaf = Split-Path $directory -Leaf
            $relative = $File.FullName.Substring($prefix.Length)
            return Join-Path $leaf $relative
        }
    }

    return $File.FullName
}

$roots = @(Resolve-ScenePaths $ScenePath)
$files = @(
    Get-SceneManifestFiles `
        -Directories $roots `
        -ModifiedSince $Since
)

Write-Host "Flock manifest directories: $($roots -join '; ')"
Write-Host "Scene manifests selected: $($files.Count)"

if ($Since -ne [datetime]::MinValue) {
    Write-Host "Modified since: $($Since.ToString('u'))"
}

if ($files.Count -eq 0) {
    Write-Error "No scene manifest files matched."
    exit 1
}

$allIssues = New-Object System.Collections.ArrayList
$summaries = New-Object System.Collections.ArrayList
$snapshotIds = @{}
$allCameraIds = @{}

foreach ($file in $files) {
    $relativeName = Get-ManifestDisplayName $file $roots
    $fileIssues = New-Object System.Collections.ArrayList
    $scene = $null

    try {
        $raw = Read-SceneManifestText $file
        $scene = $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Invalid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $scene) {
        foreach ($issue in $fileIssues) {
            [void]$allIssues.Add($issue)
        }

        [void]$summaries.Add(
            [pscustomobject]@{
                File = $relativeName
                Status = "FAIL"
                Cameras = ""
                Completeness = ""
                CaptureMs = 0.0
                CaptureMetricValid = $false
                Vehicles = 0
                Peds = 0
                Props = 0
                Projectiles = 0
                Warnings = 0
                Critical = 0
            }
        )

        continue
    }

    $issuesCommitted = $false
    $summaryAdded = $false

    try {
    foreach ($requiredProperty in @(
        "Schema",
        "SchemaVersion",
        "MinimumReaderVersion",
        "SnapshotId",
        "CapturedAtUtc",
        "Completeness",
        "World",
        "Views",
        "Vehicles",
        "Peds",
        "Props",
        "Projectiles",
        "CaptureStats"
    )) {
        if (-not (Test-HasProperty $scene $requiredProperty)) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Missing top-level property '$requiredProperty'."
        }
    }

    if ($scene.Schema -ne "flock.scene-snapshot") {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Unexpected schema '$($scene.Schema)'."
    }

    if (-not (Test-IsJsonInteger $scene.SchemaVersion)) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "SchemaVersion must be a JSON integer."
    }
    elseif ([int64]$scene.SchemaVersion -ne 1) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Unsupported schema version '$($scene.SchemaVersion)'."
    }

    if (-not (Test-IsJsonInteger $scene.MinimumReaderVersion)) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "MinimumReaderVersion must be a JSON integer."
    }
    elseif (
        [int64]$scene.MinimumReaderVersion -lt 1 -or
        [int64]$scene.MinimumReaderVersion -gt 4
    ) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Unsupported minimum reader version '$($scene.MinimumReaderVersion)'."
    }

    if ($null -eq $scene.World) {
        Add-Issue $fileIssues "Error" $relativeName "World is null."
    }

    $snapshotId = [string]$scene.SnapshotId

    if ([string]::IsNullOrWhiteSpace($snapshotId)) {
        Add-Issue $fileIssues "Error" $relativeName "SnapshotId is empty."
    }
    elseif ($snapshotIds.ContainsKey($snapshotId)) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Duplicate SnapshotId also used by '$($snapshotIds[$snapshotId])'."
    }
    else {
        $snapshotIds[$snapshotId] = $relativeName
    }

    try {
        [void][datetimeoffset]$scene.CapturedAtUtc
    }
    catch {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "CapturedAtUtc is not a valid timestamp."
    }

    $nonFinite = New-Object System.Collections.ArrayList
    Find-NonFiniteNumbers $scene '$' $nonFinite

    foreach ($jsonPath in $nonFinite) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Non-finite number at $jsonPath."
    }

    $views = @($scene.Views)
    $vehicles = @($scene.Vehicles)
    $peds = @($scene.Peds)
    $props = @($scene.Props)
    $projectiles = @($scene.Projectiles)
    $stats = $scene.CaptureStats

    if ($null -eq $stats) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "CaptureStats is null."

        $stats = [pscustomobject]@{
            CaptureMilliseconds = 0.0
            VehiclesCaptured = 0
            PedsCaptured = 0
            PropsCaptured = 0
            ProjectilesCaptured = 0
            VehiclesSkipped = 0
            PedsSkipped = 0
            PropsSkipped = 0
            ProjectilesSkipped = 0
            VehicleLimitHit = $false
            PedLimitHit = $false
            PropLimitHit = $false
            ProjectileLimitHit = $false
            Warnings = @()
            CriticalOmissions = @()
        }
    }

    if ($views.Count -eq 0) {
        Add-Issue $fileIssues "Error" $relativeName "Snapshot has no camera views."
    }

    $allIds = @{}
    $vehicleIds = @{}
    $pedIds = @{}
    $propIds = @{}
    $commonEntities = New-Object System.Collections.ArrayList

    foreach ($group in @(
        [pscustomobject]@{ Name = "Vehicle"; Items = $vehicles },
        [pscustomobject]@{ Name = "Ped"; Items = $peds },
        [pscustomobject]@{ Name = "Prop"; Items = $props },
        [pscustomobject]@{ Name = "Projectile"; Items = $projectiles }
    )) {
        $index = 0

        foreach ($item in $group.Items) {
            $common = $item.Entity
            $label = "$($group.Name)[$index]"

            if ($null -eq $common) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "$label has no Entity block."
                $index++
                continue
            }

            [void]$commonEntities.Add($common)
            $entityId = [string]$common.EntityId

            if ([string]::IsNullOrWhiteSpace($entityId)) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "$label has no EntityId."
            }
            elseif ($allIds.ContainsKey($entityId)) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Duplicate EntityId '$entityId'."
            }
            else {
                $allIds[$entityId] = $group.Name

                if ($group.Name -eq "Vehicle") {
                    $vehicleIds[$entityId] = $true
                }
                elseif ($group.Name -eq "Ped") {
                    $pedIds[$entityId] = $true
                }
                elseif ($group.Name -eq "Prop") {
                    $propIds[$entityId] = $true
                }
            }

            if ($null -eq $common.Position) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "$label '$entityId' has no position."
            }

            if ($null -eq $common.Quaternion) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "$label '$entityId' has no quaternion."
            }

            $index++
        }
    }

    $viewCameraIds = @{}

    foreach ($view in $views) {
        $cameraId = [string]$view.CameraId

        if ([string]::IsNullOrWhiteSpace($cameraId)) {
            Add-Issue $fileIssues "Error" $relativeName "A view has no CameraId."
        }
        elseif ($viewCameraIds.ContainsKey($cameraId)) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Duplicate CameraId '$cameraId' within one snapshot."
        }
        else {
            $viewCameraIds[$cameraId] = $true
            $allCameraIds[$cameraId] = $true
        }

        Test-Reference `
            $pedIds `
            ([string]$view.TargetPedId) `
            $true `
            "Camera '$cameraId' TargetPedId" `
            $fileIssues `
            $relativeName

        $vehicleTargetRequired =
            $view.TargetSemantic -eq "PlayerVehicleCenter"

        Test-Reference `
            $vehicleIds `
            ([string]$view.TargetVehicleId) `
            $vehicleTargetRequired `
            "Camera '$cameraId' TargetVehicleId" `
            $fileIssues `
            $relativeName

        if (
            $view.TargetSemantic -ne "PlayerVehicleCenter" -and
            $view.TargetSemantic -ne "PlayerPositionFallback"
        ) {
            Add-Issue `
                $fileIssues `
                "Warning" `
                $relativeName `
                "Camera '$cameraId' target semantic is '$($view.TargetSemantic)'."
        }

        [void](Test-SceneVector `
            $view.EyePosition `
            "Camera '$cameraId' EyePosition" `
            $fileIssues `
            $relativeName)
        [void](Test-SceneVector `
            $view.LookAtPosition `
            "Camera '$cameraId' LookAtPosition" `
            $fileIssues `
            $relativeName)

        $dimensionsValid =
            (Test-IsJsonInteger $view.OutputWidth) -and
            (Test-IsJsonInteger $view.OutputHeight)

        if (-not $dimensionsValid) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Camera '$cameraId' output dimensions must be JSON integers."
        }
        elseif (
            [int64]$view.OutputWidth -le 0 -or
            [int64]$view.OutputHeight -le 0
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Camera '$cameraId' has invalid output dimensions."
        }

        foreach ($numericField in @(
            "CameraHeading",
            "PhotoFieldOfViewDegrees",
            "SensingFieldOfViewDegrees",
            "SensingRangeMeters",
            "AspectRatio",
            "NearClipMeters",
            "FarClipMeters"
        )) {
            if (-not (Test-IsJsonNumber $view.$numericField)) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' $numericField must be a JSON number."
            }
        }

        if (
            (Test-IsJsonNumber $view.PhotoFieldOfViewDegrees) -and
            (
                [double]$view.PhotoFieldOfViewDegrees -le 0 -or
                [double]$view.PhotoFieldOfViewDegrees -ge 180
            )
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Camera '$cameraId' PhotoFieldOfViewDegrees must be between 0 and 180."
        }

        if (
            (Test-IsJsonNumber $view.SensingRangeMeters) -and
            [double]$view.SensingRangeMeters -le 0
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Camera '$cameraId' SensingRangeMeters must be positive."
        }

        if (
            (Test-IsJsonNumber $view.NearClipMeters) -and
            (Test-IsJsonNumber $view.FarClipMeters) -and
            (
                [double]$view.NearClipMeters -le 0 -or
                [double]$view.FarClipMeters -le
                    [double]$view.NearClipMeters
            )
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "Camera '$cameraId' clip distances are invalid."
        }

        if (
            $dimensionsValid -and
            [int64]$view.OutputWidth -gt 0 -and
            [int64]$view.OutputHeight -gt 0 -and
            (Test-IsJsonNumber $view.AspectRatio)
        ) {
            $expectedAspect =
                [double]$view.OutputWidth / [double]$view.OutputHeight

            if (
                [Math]::Abs(
                    [double]$view.AspectRatio - $expectedAspect
                ) -gt 0.01
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' AspectRatio does not match its dimensions."
            }
        }

        $destruction = $view.CameraDestruction

        if ($null -ne $destruction) {
            if (
                (Test-IsJsonInteger $scene.MinimumReaderVersion) -and
                [int64]$scene.MinimumReaderVersion -lt 2
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' destruction metadata requires MinimumReaderVersion 2."
            }

            $explosiveProperty =
                $destruction.PSObject.Properties[
                    "DestroyedByExplosiveWeapon"
                ]
            $explosiveNameProperty =
                $destruction.PSObject.Properties[
                    "DestroyingExplosiveWeapon"
                ]
            $destroyedByExplosiveWeapon = $false

            if ($null -ne $explosiveProperty) {
                if ($explosiveProperty.Value -isnot [bool]) {
                    Add-Issue `
                        $fileIssues `
                        "Error" `
                        $relativeName `
                        "Camera '$cameraId' DestroyedByExplosiveWeapon must be a JSON boolean."
                }
                else {
                    $destroyedByExplosiveWeapon =
                        [bool]$explosiveProperty.Value
                }
            }

            $explosiveWeaponName = if (
                $null -eq $explosiveNameProperty
            ) {
                ""
            }
            else {
                [string]$explosiveNameProperty.Value
            }

            if ($destroyedByExplosiveWeapon) {
                if (
                    -not (Test-IsJsonInteger `
                        $scene.MinimumReaderVersion) -or
                    [int64]$scene.MinimumReaderVersion -lt 3 -or
                    [string]::IsNullOrWhiteSpace(
                        $explosiveWeaponName
                    ) -or
                    $explosiveWeaponName.Length -gt 64
                ) {
                    Add-Issue `
                        $fileIssues `
                        "Error" `
                        $relativeName `
                        "Camera '$cameraId' has invalid explosive-destruction metadata."
                }
            }
            elseif (-not [string]::IsNullOrWhiteSpace(
                $explosiveWeaponName
            )) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' has inconsistent explosive-destruction metadata."
            }

            $weaponProperty =
                $destruction.PSObject.Properties[
                    "DestroyedByWeapon"
                ]
            $weaponHashProperty =
                $destruction.PSObject.Properties[
                    "DestroyingWeaponHash"
                ]
            $weaponNameProperty =
                $destruction.PSObject.Properties[
                    "DestroyingWeaponName"
                ]
            $destroyedByWeapon = $false

            if ($null -ne $weaponProperty) {
                if ($weaponProperty.Value -isnot [bool]) {
                    Add-Issue `
                        $fileIssues `
                        "Error" `
                        $relativeName `
                        "Camera '$cameraId' DestroyedByWeapon must be a JSON boolean."
                }
                else {
                    $destroyedByWeapon = [bool]$weaponProperty.Value
                }
            }

            $weaponHashValid =
                $null -ne $weaponHashProperty -and
                (Test-IsJsonInteger $weaponHashProperty.Value)
            $weaponHash = if ($weaponHashValid) {
                [int64]$weaponHashProperty.Value
            }
            else {
                0
            }
            $weaponName = if ($null -eq $weaponNameProperty) {
                ""
            }
            else {
                [string]$weaponNameProperty.Value
            }
            $hasWeaponHash = $weaponHashValid -and $weaponHash -ne 0
            $hasWeaponName =
                -not [string]::IsNullOrWhiteSpace($weaponName)

            if (
                (Test-IsJsonInteger $scene.MinimumReaderVersion) -and
                [int64]$scene.MinimumReaderVersion -ge 4 -and
                (
                    $null -eq $weaponProperty -or
                    -not $weaponHashValid -or
                    $null -eq $weaponNameProperty
                )
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' version-4 weapon metadata is incomplete."
            }

            if ($destroyedByWeapon) {
                if (
                    -not (Test-IsJsonInteger `
                        $scene.MinimumReaderVersion) -or
                    [int64]$scene.MinimumReaderVersion -lt 4 -or
                    $hasWeaponHash -ne $hasWeaponName -or
                    $weaponName.Length -gt 64
                ) {
                    Add-Issue `
                        $fileIssues `
                        "Error" `
                        $relativeName `
                        "Camera '$cameraId' has invalid weapon-destruction metadata."
                }
            }
            elseif ($hasWeaponHash -or $hasWeaponName) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' has inconsistent weapon-destruction metadata."
            }

            if (
                $destroyedByWeapon -and
                $destroyedByExplosiveWeapon -and
                $hasWeaponName -and
                -not [string]::Equals(
                    $weaponName,
                    $explosiveWeaponName,
                    [StringComparison]::Ordinal
                )
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' has conflicting destroying-weapon metadata."
            }

            Test-Reference `
                $propIds `
                ([string]$destruction.DestroyedPropId) `
                $true `
                "Camera '$cameraId' CameraDestruction.DestroyedPropId" `
                $fileIssues `
                $relativeName

            foreach ($vectorField in @(
                "PhysicalCameraPosition",
                "SubjectPosition",
                "CandidateEyeA",
                "CandidateEyeB"
            )) {
                [void](Test-SceneVector `
                    $destruction.$vectorField `
                    "Camera '$cameraId' CameraDestruction.$vectorField" `
                    $fileIssues `
                    $relativeName)
            }

            foreach ($numericField in @(
                "SubjectDistance",
                "RenderEyeDistance",
                "CameraLiftUnits",
                "FramingMargin"
            )) {
                if (-not (Test-IsJsonNumber $destruction.$numericField)) {
                    Add-Issue `
                        $fileIssues `
                        "Error" `
                        $relativeName `
                        "Camera '$cameraId' CameraDestruction.$numericField must be a JSON number."
                }
            }

            foreach ($integerField in @(
                "DestructionFrame",
                "CaptureFrame",
                "RequestedDelayFrames",
                "ActualDelayFrames",
                "ChosenCandidate"
            )) {
                if (-not (Test-IsJsonInteger $destruction.$integerField)) {
                    Add-Issue `
                        $fileIssues `
                        "Error" `
                        $relativeName `
                        "Camera '$cameraId' CameraDestruction.$integerField must be a JSON integer."
                }
            }

            if (
                (Test-IsJsonNumber $destruction.SubjectDistance) -and
                (
                    [double]$destruction.SubjectDistance -lt 0 -or
                    [double]$destruction.SubjectDistance -gt 40
                )
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' destruction subject distance must be between 0 and 40."
            }

            if (
                (Test-IsJsonInteger $destruction.ActualDelayFrames) -and
                (Test-IsJsonInteger $destruction.RequestedDelayFrames) -and
                (
                    [int64]$destruction.RequestedDelayFrames -lt 1 -or
                    [int64]$destruction.RequestedDelayFrames -gt 600 -or
                    [int64]$destruction.ActualDelayFrames -ne
                        [int64]$destruction.RequestedDelayFrames
                )
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' destruction delay must be exact and between 1 and 600 frames."
            }

            if (
                (Test-IsJsonInteger $destruction.CaptureFrame) -and
                (Test-IsJsonInteger $destruction.DestructionFrame) -and
                (Test-IsJsonInteger $destruction.ActualDelayFrames) -and
                (
                    [int64]$destruction.CaptureFrame -
                    [int64]$destruction.DestructionFrame
                ) -ne [int64]$destruction.ActualDelayFrames
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' destruction frame delta does not match ActualDelayFrames."
            }

            if (
                (Test-IsJsonInteger $destruction.CaptureFrame) -and
                (Test-IsJsonInteger $scene.GameFrame) -and
                [int64]$destruction.CaptureFrame -ne
                    [int64]$scene.GameFrame
            ) {
                Add-Issue `
                    $fileIssues `
                    "Error" `
                    $relativeName `
                    "Camera '$cameraId' destruction CaptureFrame does not match the scene GameFrame."
            }
        }
    }

    foreach ($vehicle in $vehicles) {
        $vehicleId = [string]$vehicle.Entity.EntityId
        $towedRequired = $null -ne $vehicle.TowedVehicleSourceHandle

        Test-Reference `
            $vehicleIds `
            ([string]$vehicle.TowedVehicleId) `
            $towedRequired `
            "Vehicle '$vehicleId' TowedVehicleId" `
            $fileIssues `
            $relativeName

        foreach ($occupant in @($vehicle.Occupants)) {
            Test-Reference `
                $pedIds `
                ([string]$occupant.PedId) `
                $true `
                "Vehicle '$vehicleId' occupant" `
                $fileIssues `
                $relativeName
        }
    }

    foreach ($ped in $peds) {
        $pedId = [string]$ped.Entity.EntityId
        $vehicleRequired = $null -ne $ped.VehicleSourceHandle

        Test-Reference `
            $vehicleIds `
            ([string]$ped.VehicleId) `
            $vehicleRequired `
            "Ped '$pedId' VehicleId" `
            $fileIssues `
            $relativeName
    }

    foreach ($projectile in $projectiles) {
        $projectileId = [string]$projectile.Entity.EntityId
        $ownerRequired = $null -ne $projectile.OwnerSourceHandle

        Test-Reference `
            $allIds `
            ([string]$projectile.OwnerEntityId) `
            $ownerRequired `
            "Projectile '$projectileId' OwnerEntityId" `
            $fileIssues `
            $relativeName
    }

    $attachmentUnavailableCount = 0

    foreach ($common in $commonEntities) {
        if ($null -ne $common.AttachedToSourceHandle) {
            Test-Reference `
                $allIds `
                ([string]$common.AttachedToEntityId) `
                $true `
                "Entity '$($common.EntityId)' AttachedToEntityId" `
                $fileIssues `
                $relativeName
        }

        if ($null -ne $common.Attachment) {
            $attachmentUnavailableCount += @(
                $common.Attachment.UnavailableFields
            ).Count
        }
    }

    foreach ($requiredStat in @(
        "CaptureMilliseconds",
        "VehiclesCaptured",
        "PedsCaptured",
        "PropsCaptured",
        "ProjectilesCaptured",
        "VehiclesSkipped",
        "PedsSkipped",
        "PropsSkipped",
        "ProjectilesSkipped",
        "VehicleLimitHit",
        "PedLimitHit",
        "PropLimitHit",
        "ProjectileLimitHit",
        "Warnings",
        "CriticalOmissions"
    )) {
        if ($null -eq $stats.PSObject.Properties[$requiredStat]) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "CaptureStats.$requiredStat is missing."
        }
    }

    foreach ($integerStat in @(
        "VehiclesCaptured",
        "PedsCaptured",
        "PropsCaptured",
        "ProjectilesCaptured",
        "VehiclesSkipped",
        "PedsSkipped",
        "PropsSkipped",
        "ProjectilesSkipped"
    )) {
        $property = $stats.PSObject.Properties[$integerStat]

        if (
            $null -ne $property -and
            -not (Test-IsJsonInteger $property.Value)
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "CaptureStats.$integerStat must be a JSON integer."
        }
    }

    $weaponObjectExclusionProperty =
        $stats.PSObject.Properties["WeaponObjectPropsExcluded"]

    if (
        $null -ne $weaponObjectExclusionProperty -and
        (
            -not (Test-IsJsonInteger `
                $weaponObjectExclusionProperty.Value) -or
            [int64]$weaponObjectExclusionProperty.Value -lt 0
        )
    ) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "CaptureStats.WeaponObjectPropsExcluded must be a non-negative JSON integer."
    }

    foreach ($booleanStat in @(
        "VehicleLimitHit",
        "PedLimitHit",
        "PropLimitHit",
        "ProjectileLimitHit"
    )) {
        $property = $stats.PSObject.Properties[$booleanStat]

        if ($null -ne $property -and $property.Value -isnot [bool]) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "CaptureStats.$booleanStat must be a JSON boolean."
        }
    }

    foreach ($arrayStat in @("Warnings", "CriticalOmissions")) {
        $property = $stats.PSObject.Properties[$arrayStat]

        if (
            $null -ne $property -and
            (
                $null -eq $property.Value -or
                $property.Value -is [string] -or
                $property.Value -isnot [System.Collections.IEnumerable]
            )
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "CaptureStats.$arrayStat must be a JSON array."
        }
    }

    $captureMetricValid =
        Test-IsJsonNumber $stats.CaptureMilliseconds

    if (-not $captureMetricValid) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "CaptureStats.CaptureMilliseconds must be a JSON number."
    }

    foreach ($countCheck in @(
        [pscustomobject]@{
            Name = "VehiclesCaptured"
            Expected = $vehicles.Count
        },
        [pscustomobject]@{
            Name = "PedsCaptured"
            Expected = $peds.Count
        },
        [pscustomobject]@{
            Name = "PropsCaptured"
            Expected = $props.Count
        },
        [pscustomobject]@{
            Name = "ProjectilesCaptured"
            Expected = $projectiles.Count
        }
    )) {
        $property = $stats.PSObject.Properties[$countCheck.Name]

        if ($null -eq $property) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "CaptureStats.$($countCheck.Name) is missing."
        }
        elseif (
            (Test-IsJsonInteger $property.Value) -and
            [int64]$property.Value -ne [int64]$countCheck.Expected
        ) {
            Add-Issue `
                $fileIssues `
                "Error" `
                $relativeName `
                "CaptureStats.$($countCheck.Name) is $($property.Value), but the array contains $($countCheck.Expected)."
        }
    }

    $captureMilliseconds = if ($captureMetricValid) {
        [double]$stats.CaptureMilliseconds
    }
    else {
        0.0
    }

    if ($captureMilliseconds -lt 0) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "CaptureMilliseconds is negative."
    }
    elseif ($captureMilliseconds -gt $WarnCaptureMilliseconds) {
        Add-Issue `
            $fileIssues `
            "Warning" `
            $relativeName `
            "Capture took $([Math]::Round($captureMilliseconds, 1)) ms (warning threshold: $WarnCaptureMilliseconds ms)."
    }

    $warnings = @($stats.Warnings)
    $criticalOmissions = @($stats.CriticalOmissions)

    foreach ($warning in $warnings) {
        Add-Issue `
            $fileIssues `
            "Warning" `
            $relativeName `
            "Recorder warning: $warning"
    }

    foreach ($critical in $criticalOmissions) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Critical omission: $critical"
    }

    $skipped =
        [int]$stats.VehiclesSkipped +
        [int]$stats.PedsSkipped +
        [int]$stats.PropsSkipped +
        [int]$stats.ProjectilesSkipped

    if ($skipped -gt 0) {
        Add-Issue `
            $fileIssues `
            "Warning" `
            $relativeName `
            "$skipped entities were skipped during capture."
    }

    $limitNames = @(
        "VehicleLimitHit",
        "PedLimitHit",
        "PropLimitHit",
        "ProjectileLimitHit"
    )
    $limitsHit = @(
        $limitNames |
        Where-Object {
            $stats.$_ -is [bool] -and [bool]$stats.$_
        }
    )

    if ($limitsHit.Count -gt 0) {
        Add-Issue `
            $fileIssues `
            "Warning" `
            $relativeName `
            "Capture limits hit: $($limitsHit -join ', ')."
    }

    $worldUnavailable = @()

    if ($null -ne $scene.World) {
        $worldUnavailable = @(
            $scene.World.UnavailableFields |
            Where-Object { $null -ne $_ }
        )
    }
    $viewUnavailableCount = 0

    foreach ($view in $views) {
        $viewUnavailableCount += @($view.UnavailableFields).Count
    }

    if (
        $worldUnavailable.Count -gt 0 -or
        $viewUnavailableCount -gt 0 -or
        $attachmentUnavailableCount -gt 0
    ) {
        Add-Issue `
            $fileIssues `
            "Warning" `
            $relativeName `
            "Unavailable world/view/attachment fields: $($worldUnavailable.Count + $viewUnavailableCount + $attachmentUnavailableCount)."
    }

    $documentedPartialReasons =
        $warnings.Count +
        $criticalOmissions.Count +
        $skipped +
        $limitsHit.Count +
        $worldUnavailable.Count +
        $viewUnavailableCount +
        $attachmentUnavailableCount

    if (
        $scene.Completeness -eq "BestEffort" -and
        $documentedPartialReasons -gt 0
    ) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Completeness is BestEffort despite documented capture omissions."
    }
    elseif (
        $scene.Completeness -eq "Partial" -and
        $documentedPartialReasons -eq 0
    ) {
        Add-Issue `
            $fileIssues `
            "Warning" `
            $relativeName `
            "Completeness is Partial, but no reason was recorded."
    }
    elseif ($scene.Completeness -notin @("BestEffort", "Partial")) {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Unknown completeness value '$($scene.Completeness)'."
    }

    $errorCount = @(
        $fileIssues |
        Where-Object { $_.Severity -eq "Error" }
    ).Count
    $warningCount = @(
        $fileIssues |
        Where-Object { $_.Severity -eq "Warning" }
    ).Count

    $status = if ($errorCount -gt 0) {
        "FAIL"
    }
    elseif ($warningCount -gt 0) {
        "WARN"
    }
    else {
        "PASS"
    }

    foreach ($issue in $fileIssues) {
        [void]$allIssues.Add($issue)
    }
    $issuesCommitted = $true

    [void]$summaries.Add(
        [pscustomobject]@{
            File = $relativeName
            Status = $status
            Cameras = (@($viewCameraIds.Keys) -join ", ")
            Completeness = [string]$scene.Completeness
            CaptureMs = [Math]::Round($captureMilliseconds, 1)
            CaptureMetricValid = $captureMetricValid
            Vehicles = $vehicles.Count
            Peds = $peds.Count
            Props = $props.Count
            Projectiles = $projectiles.Count
            Warnings = $warningCount
            Critical = $criticalOmissions.Count
        }
    )
    $summaryAdded = $true
    }
    catch {
        Add-Issue `
            $fileIssues `
            "Error" `
            $relativeName `
            "Validation stopped for this file: $($_.Exception.Message)"

        if ($issuesCommitted) {
            [void]$allIssues.Add($fileIssues[$fileIssues.Count - 1])
        }
        else {
            foreach ($issue in $fileIssues) {
                [void]$allIssues.Add($issue)
            }
        }

        if (-not $summaryAdded) {
            [void]$summaries.Add(
                [pscustomobject]@{
                    File = $relativeName
                    Status = "FAIL"
                    Cameras = ""
                    Completeness = [string]$scene.Completeness
                    CaptureMs = 0.0
                    CaptureMetricValid = $false
                    Vehicles = 0
                    Peds = 0
                    Props = 0
                    Projectiles = 0
                    Warnings = 0
                    Critical = 0
                }
            )
        }
    }
}

Write-Host ""
Write-Host "Per-scene results"
$summaries |
    Format-Table `
        Status,
        CaptureMs,
        Vehicles,
        Peds,
        Props,
        Projectiles,
        Completeness,
        Cameras,
        File `
        -AutoSize |
    Out-Host

$failCount = @($summaries | Where-Object { $_.Status -eq "FAIL" }).Count
$warnCount = @($summaries | Where-Object { $_.Status -eq "WARN" }).Count
$passCount = @($summaries | Where-Object { $_.Status -eq "PASS" }).Count
$timedSummaries = @(
    $summaries |
    Where-Object { $_.CaptureMetricValid }
)
$captureTimes = [double[]]@(
    $timedSummaries |
    ForEach-Object { $_.CaptureMs }
)

if ($captureTimes.Count -gt 0) {
    $captureAverage = (
        $captureTimes |
        Measure-Object -Average
    ).Average
    $captureMaximum = (
        $captureTimes |
        Measure-Object -Maximum
    ).Maximum
}
else {
    $captureAverage = 0.0
    $captureMaximum = 0.0
}

Write-Host ""
Write-Host "Aggregate"
[pscustomobject]@{
    Files = $summaries.Count
    Passed = $passCount
    Warnings = $warnCount
    Failed = $failCount
    UniqueCameras = $allCameraIds.Count
    TimedFiles = $timedSummaries.Count
    CaptureAverageMs = [Math]::Round([double]$captureAverage, 1)
    CaptureP50Ms = [Math]::Round(
        (Get-Percentile $captureTimes 50),
        1
    )
    CaptureP95Ms = [Math]::Round(
        (Get-Percentile $captureTimes 95),
        1
    )
    CaptureMaxMs = [Math]::Round(
        [double]$captureMaximum,
        1
    )
} | Format-List | Out-Host

Write-Host "Slowest scenes"
$timedSummaries |
    Sort-Object CaptureMs -Descending |
    Select-Object -First 5 File, CaptureMs, Cameras, Vehicles, Peds, Props |
    Format-Table -AutoSize |
    Out-Host

if ($allIssues.Count -gt 0) {
    Write-Host ""
    Write-Host "Issues"
    $allIssues |
        Sort-Object Severity, File, Message |
        Format-Table Severity, File, Message -Wrap -AutoSize |
        Out-Host
}

if ($failCount -gt 0) {
    exit 1
}

if ($FailOnWarning -and $warnCount -gt 0) {
    exit 2
}

exit 0
