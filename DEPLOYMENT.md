# Deployment Script Quick Reference

## Prerequisites

1. **Install Power Platform CLI**
   ```powershell
   # Download from: https://aka.ms/PowerPlatformCLI
   # Or install via winget:
   winget install Microsoft.PowerPlatformCLI
   ```

2. **Authenticate to Power Platform**
   ```powershell
   # Create a new authentication profile
   pac auth create
   
   # List existing profiles
   pac auth list
   
   # Select a profile
   pac auth select --index 1
   ```

3. **Configure Environment**
   ```powershell
   # Copy the example file
   copy .env.example .env
   
   # Edit .env and add your values:
   # - POWER_PLATFORM_ENVIRONMENT (GUID or URL)
   # - SOLUTION_UNIQUE_NAME (optional)
   ```

## Usage Examples

### Basic Deployment
```powershell
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"
```

### Deploy to Specific Environment
```powershell
# Using environment GUID
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced" -Environment "12345678-1234-1234-1234-123456789012"

# Using environment URL
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced" -Environment "https://yourorg.crm.dynamics.com"
```

### Deploy to Solution
```powershell
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced" -SolutionUniqueName "CustomConnectors"
```

### Full Override (Environment + Solution)
```powershell
.\deploy-connector.ps1 `
  -ConnectorName "HttpRequestAdvanced" `
  -Environment "https://yourorg.crm.dynamics.com" `
  -SolutionUniqueName "CustomConnectors"
```

## Environment File (.env)

### Required Variables

```bash
# Your Power Platform environment
# Can be either a GUID or full URL
POWER_PLATFORM_ENVIRONMENT=12345678-1234-1234-1234-123456789012
# OR
POWER_PLATFORM_ENVIRONMENT=https://yourorg.crm.dynamics.com

# Optional: Solution to add connector to
SOLUTION_UNIQUE_NAME=CustomConnectors
```

### How to Find Values

**Environment GUID/URL:**
1. Go to [Power Platform Admin Center](https://admin.powerplatform.microsoft.com/)
2. Select your environment
3. Copy the Environment ID (GUID) or Environment URL

**Solution Unique Name:**
1. Go to [Power Apps](https://make.powerapps.com/)
2. Select **Solutions**
3. Open your solution
4. The unique name is shown in the solution details (not the display name)

## Script Behavior

### File Validation
The script validates these files exist:
- ✅ `connectors/{ConnectorName}/apiDefinition.swagger.yaml` (required)
- ✅ `connectors/{ConnectorName}/script.csx` (required)
- ⚠️ `connectors/{ConnectorName}/icon.png` (optional - warning if missing)

### Configuration Priority
Parameters override .env file values:
1. **Command-line parameters** (highest priority)
2. **.env file values**
3. **Default behavior** (uses current auth profile)

### Output Stages

**Stage 1: Validation**
- Checks if connector folder exists
- Verifies required files are present

**Stage 2: Configuration**
- Loads .env file
- Resolves environment and solution settings
- Shows what will be deployed

**Stage 3: Preparation**
- Lists all files that will be included
- Shows optional files (icon, API properties)

**Stage 4: Deployment**
- Executes `pac connector create`
- Shows the full command being run
- Displays deployment result

## Troubleshooting

### "Connector folder not found"
```
Error: Connector folder not found: C:\...\connectors\YourConnector

Solution: Check spelling of connector name (case-sensitive)
```

### "Missing required file: apiDefinition.swagger.yaml"
```
Error: Missing required file: apiDefinition.swagger.yaml

Solution: Ensure the connector has all three required files
```

### "Must provide either --settings-file or --api-definition-file"
```
Error: This error shouldn't occur with the script, but if it does:

Solution: The script uses --api-definition-file approach, not --settings-file
```

### Authentication Errors
```
Error: No auth profiles found

Solution: Run 'pac auth create' to authenticate to Power Platform
```

### Environment Not Found
```
Error: Environment '...' not found

Solution:
1. Verify POWER_PLATFORM_ENVIRONMENT value in .env
2. Ensure you're authenticated to the right tenant
3. Check you have permissions to the environment
```

### Solution Not Found
```
Error: Solution '...' not found

Solution:
1. Verify SOLUTION_UNIQUE_NAME in .env (use unique name, not display name)
2. Ensure solution exists in target environment
3. Check you have permissions to the solution
```

## Advanced Usage

### Deploy Without Solution
```powershell
# Leave SOLUTION_UNIQUE_NAME empty in .env
# Or explicitly override:
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced" -SolutionUniqueName ""
```

### Use Current Auth Profile Environment
```powershell
# Leave POWER_PLATFORM_ENVIRONMENT empty in .env
# Script will use the active environment from your auth profile
.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"
```

### Get Help
```powershell
Get-Help .\deploy-connector.ps1 -Detailed
```

## pac CLI Reference

### Useful Commands

```powershell
# List all connectors in environment
pac connector list

# Get connector details
pac connector download --connector-id <guid>

# Update existing connector
pac connector update

# Delete connector
pac connector delete --connector-id <guid>

# List environments
pac org list

# Switch environment
pac org select --environment <guid-or-url>
```

## Files Created by This Setup

```
pa-cs-custom-connectors/
├── deploy-connector.ps1      # Main deployment script
├── .env.example              # Configuration template
├── .env                      # Your configuration (gitignored)
└── DEPLOYMENT.md            # This file
```

## Next Steps

1. ✅ Install Power Platform CLI
2. ✅ Run `pac auth create` to authenticate
3. ✅ Copy `.env.example` to `.env`
4. ✅ Edit `.env` with your environment details
5. ✅ Run `.\deploy-connector.ps1 -ConnectorName "HttpRequestAdvanced"`
6. ✅ Verify connector in Power Automate portal

## Additional Resources

- [Power Platform CLI Documentation](https://learn.microsoft.com/en-us/power-platform/developer/cli/introduction)
- [Custom Connectors Documentation](https://learn.microsoft.com/en-us/connectors/custom-connectors/)
- [pac connector commands](https://learn.microsoft.com/en-us/power-platform/developer/cli/reference/connector)
