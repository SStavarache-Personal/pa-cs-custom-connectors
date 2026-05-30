# ============================================================================
# Power Automate Custom Connector Deployment Script
# ============================================================================
# Deploys (creates or updates) a custom connector to Power Platform using pac CLI.
#
# Usage:
#   .\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"
#   .\deploy-connector.ps1 -ConnectorName "YourConnector" -Environment "guid-or-url"
#   .\deploy-connector.ps1 -ConnectorName "YourConnector" -ConnectorId "guid"
#
# Behaviour:
#   - If -ConnectorId is provided, the script updates that connector directly.
#   - Otherwise it attempts to create a new connector. If creation fails because
#     the connector already exists, the script automatically looks up the existing
#     connector ID via 'pac connector list' and retries as an update.
#
# Prerequisites:
#   - Microsoft Power Platform CLI (pac) installed
#   - Authenticated to Power Platform (run: pac auth create)
#   - .env file configured with environment settings
# ============================================================================

param(
    [Parameter(Mandatory=$true, HelpMessage="Name of the connector folder to deploy")]
    [string]$ConnectorName,
    
    [Parameter(Mandatory=$false, HelpMessage="Override environment from .env file")]
    [string]$Environment = "",
    
    [Parameter(Mandatory=$false, HelpMessage="Override solution name from .env file")]
    [string]$SolutionUniqueName = "",

    [Parameter(Mandatory=$false, HelpMessage="Connector ID for updating an existing connector. When provided the script uses 'pac connector update' instead of 'pac connector create'.")]
    [string]$ConnectorId = ""
)

# Set error action preference
$ErrorActionPreference = "Stop"

# ============================================================================
# FUNCTIONS
# ============================================================================

function Write-ColorOutput {
    param(
        [string]$Message,
        [string]$Color = "White"
    )
    Write-Host $Message -ForegroundColor $Color
}

function Load-EnvFile {
    param([string]$EnvFilePath)
    
    if (-not (Test-Path $EnvFilePath)) {
        Write-ColorOutput "Warning: .env file not found at $EnvFilePath" "Yellow"
        Write-ColorOutput "Using .env.example as reference. Please create .env file." "Yellow"
        return @{}
    }
    
    $envVars = @{}
    Get-Content $EnvFilePath | ForEach-Object {
        $line = $_.Trim()
        
        # Skip empty lines and comments
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
            return
        }
        
        # Parse KEY=VALUE
        if ($line -match '^([^=]+)=(.*)$') {
            $key = $matches[1].Trim()
            $value = $matches[2].Trim()
            
            # Remove quotes if present
            $value = $value -replace '^["'']|["'']$', ''
            
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $envVars[$key] = $value
            }
        }
    }
    
    return $envVars
}

function Test-ConnectorFiles {
    param(
        [string]$ConnectorPath,
        [string]$ConnectorName
    )
    
    $errors = @()
    
    # Check if connector folder exists
    if (-not (Test-Path $ConnectorPath)) {
        $errors += "Connector folder not found: $ConnectorPath"
        return $errors
    }
    
    # Check required files
    $apiDefFileJson = Join-Path $ConnectorPath "apiDefinition.swagger.json"
    $apiDefFileYaml = Join-Path $ConnectorPath "apiDefinition.swagger.yaml"
    $apiPropertiesFile = Join-Path $ConnectorPath "apiProperties.json"
    $scriptFile = Join-Path $ConnectorPath "script.csx"
    
    if (-not ((Test-Path $apiDefFileJson) -or (Test-Path $apiDefFileYaml))) {
        $errors += "Missing required file: apiDefinition.swagger.json or apiDefinition.swagger.yaml"
    }
    
    if (-not (Test-Path $apiPropertiesFile)) {
        $errors += "Missing required file: apiProperties.json"
    }
    
    if (-not (Test-Path $scriptFile)) {
        $errors += "Missing required file: script.csx"
    }
    
    return $errors
}

function Convert-CommandOutputToString {
    param([object[]]$CommandOutput)

    if ($null -eq $CommandOutput) {
        return ""
    }

    return ($CommandOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
}

function Test-IsExpiredAuthTokenError {
    param([object[]]$CommandOutput)

    $outputText = Convert-CommandOutputToString -CommandOutput $CommandOutput

    return ($outputText -match "AADSTS70043") -or
           ($outputText -match "refresh token has expired or is invalid")
}

function Invoke-PacConnectorCreate {
    param([string[]]$PacArgs)

    $output = & pac connector create @PacArgs 2>&1
    $exitCode = $LASTEXITCODE

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-PacConnectorUpdate {
    param([string[]]$PacArgs)

    $output = & pac connector update @PacArgs 2>&1
    $exitCode = $LASTEXITCODE

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Test-IsConnectorExistsError {
    param([object[]]$CommandOutput)

    $outputText = Convert-CommandOutputToString -CommandOutput $CommandOutput

    return $outputText -match "already exists in the org"
}

function Get-ExistingConnectorId {
    param(
        [string]$ApiDefFile,
        [string]$TargetEnvironment
    )

    # Read the connector title from the swagger definition to derive its
    # logical name, then match it against 'pac connector list' output.
    try {
        $swaggerJson = Get-Content -Path $ApiDefFile -Raw | ConvertFrom-Json
        $connectorTitle = $swaggerJson.info.title
    }
    catch {
        Write-ColorOutput "  [!] Could not read connector title from API definition." "Yellow"
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($connectorTitle)) {
        Write-ColorOutput "  [!] Connector title is empty in API definition." "Yellow"
        return $null
    }

    # Build the logical name the same way Power Platform does:
    # lowercase, spaces become -20, prefixed with new_
    $logicalName = "new_" + ($connectorTitle.ToLower() -replace ' ', '-20')

    Write-ColorOutput "  [>] Looking up existing connector (logical name: $logicalName)..." "Gray"

    $listArgs = @("connector", "list")
    if ($TargetEnvironment) {
        $listArgs += "--environment"
        $listArgs += $TargetEnvironment
    }

    $listOutput = & pac @listArgs 2>&1
    $listExitCode = $LASTEXITCODE

    if ($listExitCode -ne 0) {
        Write-ColorOutput "  [!] Failed to list connectors." "Yellow"
        return $null
    }

    # Parse the table output — each line that starts with a GUID is a connector row
    foreach ($line in $listOutput) {
        $lineStr = $line.ToString()
        if ($lineStr -match '^\s*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+(\S+)') {
            $id = $matches[1]
            $name = $matches[2]
            if ($name -eq $logicalName) {
                return $id
            }
        }
    }

    Write-ColorOutput "  [!] Could not find connector with logical name '$logicalName' in the environment." "Yellow"
    return $null
}

function Invoke-PacReauthentication {
    param([string]$TargetEnvironment)

    $authArgs = @("auth", "create")
    if ($TargetEnvironment) {
        $authArgs += "--environment"
        $authArgs += $TargetEnvironment
    }

    Write-ColorOutput "  [!] Authentication token appears expired. Starting re-authentication..." "Yellow"
    Write-ColorOutput "  [!] Complete the sign-in flow if prompted." "Yellow"

    $output = & pac @authArgs 2>&1
    $exitCode = $LASTEXITCODE

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

# ============================================================================
# MAIN SCRIPT
# ============================================================================

Write-ColorOutput "`n================================================" "Cyan"
Write-ColorOutput "  Power Automate Connector Deployment" "Cyan"
Write-ColorOutput "================================================`n" "Cyan"

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$connectorsDir = Join-Path $scriptDir "connectors"
$connectorPath = Join-Path $connectorsDir $ConnectorName

Write-ColorOutput "Connector: $ConnectorName" "White"
Write-ColorOutput "Path: $connectorPath`n" "Gray"

# ============================================================================
# STEP 1: Validate Connector Files
# ============================================================================

Write-ColorOutput "[1/4] Validating connector files..." "Yellow"

$validationErrors = Test-ConnectorFiles -ConnectorPath $connectorPath -ConnectorName $ConnectorName

if ($validationErrors.Count -gt 0) {
    Write-ColorOutput "`nValidation Errors:" "Red"
    foreach ($error in $validationErrors) {
        Write-ColorOutput "  [X] $error" "Red"
    }
    Write-ColorOutput "`nDeployment aborted.`n" "Red"
    exit 1
}

Write-ColorOutput "  [OK] All required files found" "Green"

# ============================================================================
# STEP 2: Load Configuration
# ============================================================================

Write-ColorOutput "`n[2/4] Loading configuration..." "Yellow"

$envFile = Join-Path $scriptDir ".env"
$envVars = Load-EnvFile -EnvFilePath $envFile

# Determine environment (parameter overrides .env)
if ($Environment) { 
    $targetEnvironment = $Environment 
} elseif ($envVars.ContainsKey("POWER_PLATFORM_ENVIRONMENT")) { 
    $targetEnvironment = $envVars["POWER_PLATFORM_ENVIRONMENT"] 
} else { 
    $targetEnvironment = "" 
}

# Determine solution (parameter overrides .env)
if ($SolutionUniqueName) { 
    $targetSolution = $SolutionUniqueName 
} elseif ($envVars.ContainsKey("SOLUTION_UNIQUE_NAME")) { 
    $targetSolution = $envVars["SOLUTION_UNIQUE_NAME"] 
} else { 
    $targetSolution = "" 
}

if ($targetEnvironment) {
    Write-ColorOutput "  [OK] Environment: $targetEnvironment" "Green"
} else {
    Write-ColorOutput "  [!] Environment: Using current auth profile default" "Yellow"
}

if ($targetSolution) {
    Write-ColorOutput "  [OK] Solution: $targetSolution" "Green"
} else {
    Write-ColorOutput "  [!] Solution: Connector will not be added to a solution" "Yellow"
}

# ============================================================================
# STEP 3: Prepare File Paths
# ============================================================================

Write-ColorOutput "`n[3/4] Preparing deployment files..." "Yellow"

# Check for API definition file (.json or .yaml)
$apiDefFile = Join-Path $connectorPath "apiDefinition.swagger.json"
if (-not (Test-Path $apiDefFile)) {
    $apiDefFile = Join-Path $connectorPath "apiDefinition.swagger.yaml"
}

$apiPropertiesFile = Join-Path $connectorPath "apiProperties.json"
$scriptFile = Join-Path $connectorPath "script.csx"
$iconFile = Join-Path $connectorPath "icon.png"

# Check required and optional files
$hasApiProperties = Test-Path $apiPropertiesFile
$hasIcon = Test-Path $iconFile

Write-ColorOutput "  [OK] API Definition: $(Split-Path -Leaf $apiDefFile)" "Green"
Write-ColorOutput "  [OK] Script: script.csx" "Green"

if ($hasApiProperties) {
    Write-ColorOutput "  [OK] API Properties: apiProperties.json" "Green"
} else {
    Write-ColorOutput "  [!] API Properties: apiProperties.json NOT FOUND (required)" "Red"
}

if ($hasIcon) {
    Write-ColorOutput "  [OK] Icon: icon.png (optional)" "Green"
} else {
    Write-ColorOutput "  [!] Icon: Not found (optional)" "Gray"
}

# Check if apiProperties is missing (it's required)
if (-not $hasApiProperties) {
    Write-ColorOutput "`n[ERROR] apiProperties.json is required but not found!" "Red"
    Write-ColorOutput "Run 'pac connector init' in the connector folder to create it, or copy from template." "Yellow"
    Write-ColorOutput "`nDeployment aborted.`n" "Red"
    exit 1
}

# ============================================================================
# STEP 4: Execute Deployment
# ============================================================================

# Determine deployment mode
$isUpdate = -not [string]::IsNullOrWhiteSpace($ConnectorId)

if ($isUpdate) {
    Write-ColorOutput "`n[4/4] Updating existing connector ($ConnectorId)..." "Yellow"
} else {
    Write-ColorOutput "`n[4/4] Deploying connector to Power Platform..." "Yellow"
}

# Build shared file arguments (used by both create and update)
$fileArgs = @()

# Add environment if specified
if ($targetEnvironment) {
    $fileArgs += "--environment"
    $fileArgs += $targetEnvironment
}

# Add required files
$fileArgs += "--api-definition-file"
$fileArgs += $apiDefFile

$fileArgs += "--api-properties-file"
$fileArgs += $apiPropertiesFile

$fileArgs += "--script-file"
$fileArgs += $scriptFile

# Add optional files
if ($hasIcon) {
    $fileArgs += "--icon-file"
    $fileArgs += $iconFile
}

# Add solution if specified
if ($targetSolution) {
    $fileArgs += "--solution-unique-name"
    $fileArgs += $targetSolution
}

# ---- Helper: display and run a deployment command ----
function Invoke-Deployment {
    param(
        [string]$Mode,          # "create" or "update"
        [string[]]$FileArgs,
        [string]$ConnectorId    # required for update
    )

    $pacArgs = $FileArgs.Clone()

    if ($Mode -eq "update") {
        $pacArgs += "--connector-id"
        $pacArgs += $ConnectorId
        $displayCommand = "pac connector update"
    } else {
        $displayCommand = "pac connector create"
    }

    # Display command
    $displayArgs = ($pacArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $fullCommand = "$displayCommand $($displayArgs -join ' ')"
    Write-ColorOutput "`nExecuting command:" "Gray"
    Write-ColorOutput $fullCommand "Gray"
    Write-ColorOutput ""

    if ($Mode -eq "update") {
        return Invoke-PacConnectorUpdate -PacArgs $pacArgs
    } else {
        return Invoke-PacConnectorCreate -PacArgs $pacArgs
    }
}

# ---- Helper: handle a successful result ----
function Write-DeploymentSuccess {
    param(
        [string]$Suffix,
        [object[]]$Output
    )
    $label = if ($Suffix) { "  [SUCCESS] Deployment Successful ($Suffix)!" } else { "  [SUCCESS] Deployment Successful!" }
    Write-ColorOutput "`n================================================" "Green"
    Write-ColorOutput $label "Green"
    Write-ColorOutput "================================================`n" "Green"
    Write-ColorOutput "Output:" "White"
    Write-Output $Output
}

# ---- Helper: switch from create to update when the connector already exists ----
function Invoke-ExistingConnectorUpdate {
    param(
        [string]$ApiDefFile,
        [string]$TargetEnvironment,
        [string[]]$FileArgs
    )

    Write-ColorOutput "`n  [!] Connector already exists. Switching to update mode..." "Yellow"

    $existingId = Get-ExistingConnectorId -ApiDefFile $ApiDefFile -TargetEnvironment $TargetEnvironment

    if (-not $existingId) {
        Write-ColorOutput "`n================================================" "Red"
        Write-ColorOutput "  [FAILED] Could Not Resolve Existing Connector ID" "Red"
        Write-ColorOutput "================================================`n" "Red"
        Write-ColorOutput "The connector already exists but we could not determine its ID automatically." "Red"
        Write-ColorOutput "Re-run with -ConnectorId `"<guid>`" to update it explicitly." "Yellow"
        exit 1
    }

    Write-ColorOutput "  [OK] Found existing connector: $existingId" "Green"

    $updateAttempt = Invoke-Deployment -Mode "update" -FileArgs $FileArgs -ConnectorId $existingId

    if ($updateAttempt.ExitCode -eq 0) {
        Write-DeploymentSuccess -Suffix "updated" -Output $updateAttempt.Output
        return
    }

    Write-ColorOutput "`n================================================" "Red"
    Write-ColorOutput "  [FAILED] Update Failed" "Red"
    Write-ColorOutput "================================================`n" "Red"
    Write-ColorOutput "Error Output:" "Red"
    Write-Output $updateAttempt.Output
    exit 1
}

# ---- Main deployment logic ----
try {
    if ($isUpdate) {
        # Explicit update mode — go straight to update
        $attempt = Invoke-Deployment -Mode "update" -FileArgs $fileArgs -ConnectorId $ConnectorId

        if ($attempt.ExitCode -eq 0) {
            Write-DeploymentSuccess -Suffix "update" -Output $attempt.Output
        } elseif (Test-IsExpiredAuthTokenError -CommandOutput $attempt.Output) {
            $authAttempt = Invoke-PacReauthentication -TargetEnvironment $targetEnvironment
            if ($authAttempt.ExitCode -ne 0) {
                Write-ColorOutput "`n================================================" "Red"
                Write-ColorOutput "  [FAILED] Re-authentication Failed" "Red"
                Write-ColorOutput "================================================`n" "Red"
                Write-Output $authAttempt.Output
                exit 1
            }
            Write-ColorOutput "  [OK] Re-authentication completed. Retrying update..." "Green"
            $retry = Invoke-Deployment -Mode "update" -FileArgs $fileArgs -ConnectorId $ConnectorId
            if ($retry.ExitCode -eq 0) {
                Write-DeploymentSuccess -Suffix "update after re-auth" -Output $retry.Output
            } else {
                Write-ColorOutput "`n================================================" "Red"
                Write-ColorOutput "  [FAILED] Update Failed After Re-authentication" "Red"
                Write-ColorOutput "================================================`n" "Red"
                Write-ColorOutput "Error Output:" "Red"
                Write-Output $retry.Output
                exit 1
            }
        } else {
            Write-ColorOutput "`n================================================" "Red"
            Write-ColorOutput "  [FAILED] Update Failed" "Red"
            Write-ColorOutput "================================================`n" "Red"
            Write-ColorOutput "Error Output:" "Red"
            Write-Output $attempt.Output
            exit 1
        }
    } else {
        # Auto mode — try create first, fall back to update if connector exists
        $attempt = Invoke-Deployment -Mode "create" -FileArgs $fileArgs

        if ($attempt.ExitCode -eq 0) {
            Write-DeploymentSuccess -Suffix "created" -Output $attempt.Output
        } elseif (Test-IsExpiredAuthTokenError -CommandOutput $attempt.Output) {
            $authAttempt = Invoke-PacReauthentication -TargetEnvironment $targetEnvironment
            if ($authAttempt.ExitCode -ne 0) {
                Write-ColorOutput "`n================================================" "Red"
                Write-ColorOutput "  [FAILED] Re-authentication Failed" "Red"
                Write-ColorOutput "================================================`n" "Red"
                Write-Output $authAttempt.Output
                exit 1
            }
            Write-ColorOutput "  [OK] Re-authentication completed. Retrying create..." "Green"
            $retry = Invoke-Deployment -Mode "create" -FileArgs $fileArgs
            if ($retry.ExitCode -eq 0) {
                Write-DeploymentSuccess -Suffix "created after re-auth" -Output $retry.Output
            } elseif (Test-IsConnectorExistsError -CommandOutput $retry.Output) {
                Invoke-ExistingConnectorUpdate -ApiDefFile $apiDefFile -TargetEnvironment $targetEnvironment -FileArgs $fileArgs
            } else {
                Write-ColorOutput "`n================================================" "Red"
                Write-ColorOutput "  [FAILED] Deployment Failed After Re-authentication" "Red"
                Write-ColorOutput "================================================`n" "Red"
                Write-ColorOutput "Error Output:" "Red"
                Write-Output $retry.Output
                exit 1
            }
        } elseif (Test-IsConnectorExistsError -CommandOutput $attempt.Output) {
            Invoke-ExistingConnectorUpdate -ApiDefFile $apiDefFile -TargetEnvironment $targetEnvironment -FileArgs $fileArgs
        } else {
            Write-ColorOutput "`n================================================" "Red"
            Write-ColorOutput "  [FAILED] Deployment Failed" "Red"
            Write-ColorOutput "================================================`n" "Red"
            Write-ColorOutput "Error Output:" "Red"
            Write-Output $attempt.Output
            exit 1
        }
    }
}
catch {
    Write-ColorOutput "`n================================================" "Red"
    Write-ColorOutput "  [FAILED] Deployment Failed" "Red"
    Write-ColorOutput "================================================`n" "Red"
    Write-ColorOutput "Error: $_" "Red"
    exit 1
}

Write-ColorOutput "`nDeployment completed successfully!`n" "Green"
