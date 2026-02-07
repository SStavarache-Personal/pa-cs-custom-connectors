# ============================================================================
# Power Automate Custom Connector Deployment Script
# ============================================================================
# Deploys a custom connector to Power Platform using pac CLI
#
# Usage:
#   .\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"
#   .\deploy-connector.ps1 -ConnectorName "YourConnector" -Environment "guid-or-url"
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
    [string]$SolutionUniqueName = ""
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

Write-ColorOutput "`n[4/4] Deploying connector to Power Platform..." "Yellow"

# Build pac command
$pacCommand = "pac connector create"
$pacArgs = @()

# Add environment if specified
if ($targetEnvironment) {
    $pacArgs += "--environment"
    $pacArgs += "`"$targetEnvironment`""
}

# Add required files
$pacArgs += "--api-definition-file"
$pacArgs += "`"$apiDefFile`""

$pacArgs += "--api-properties-file"
$pacArgs += "`"$apiPropertiesFile`""

$pacArgs += "--script-file"
$pacArgs += "`"$scriptFile`""

# Add optional files
if ($hasIcon) {
    $pacArgs += "--icon-file"
    $pacArgs += "`"$iconFile`""
}

# Add solution if specified
if ($targetSolution) {
    $pacArgs += "--solution-unique-name"
    $pacArgs += "`"$targetSolution`""
}

# Display command
$fullCommand = "$pacCommand $($pacArgs -join ' ')"
Write-ColorOutput "`nExecuting command:" "Gray"
Write-ColorOutput $fullCommand "Gray"
Write-ColorOutput ""

# Execute deployment
try {
    $result = & pac connector create @pacArgs 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-ColorOutput "`n================================================" "Green"
        Write-ColorOutput "  [SUCCESS] Deployment Successful!" "Green"
        Write-ColorOutput "================================================`n" "Green"
        Write-ColorOutput "Output:" "White"
        Write-Output $result
    } else {
        Write-ColorOutput "`n================================================" "Red"
        Write-ColorOutput "  [FAILED] Deployment Failed" "Red"
        Write-ColorOutput "================================================`n" "Red"
        Write-ColorOutput "Error Output:" "Red"
        Write-Output $result
        exit 1
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
