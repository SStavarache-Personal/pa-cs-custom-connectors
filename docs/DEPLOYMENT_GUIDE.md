# Deployment Guide

This guide provides step-by-step instructions for manually deploying custom connectors to Power Automate by copy-pasting the Swagger definition and C# script.

---

## 📋 Prerequisites

Before deploying a connector, ensure you have:

- [ ] A Power Automate account with appropriate permissions
- [ ] The connector's `apiDefinition.swagger.yaml` file
- [ ] The connector's `script.csx` file
- [ ] Any required API keys or credentials for the target API

---

## 🚀 Deployment Steps

### Step 1: Access Custom Connectors

1. Sign in to [Power Automate](https://make.powerautomate.com)
2. In the left navigation, expand **Data**
3. Select **Custom connectors**

![Navigation to Custom Connectors](https://learn.microsoft.com/en-us/connectors/custom-connectors/media/define-openapi-definition/data-custom-connectors.png)

---

### Step 2: Create New Connector

1. Click **+ New custom connector** in the top right
2. Select **Import an OpenAPI file**
3. Enter a name for your connector (use the folder name from the repository)
4. Click **Import** and select the `apiDefinition.swagger.yaml` file
5. Click **Continue**

> **Alternative:** You can also select **Create from blank** if you prefer to manually configure each setting.

---

### Step 3: Review General Settings

On the **General** tab, verify:

| Field | Expected Value |
|-------|----------------|
| Icon | Upload `icon.png` if available (32x32 or 64x64) |
| Icon background color | Choose a color (not white #ffffff) |
| Description | Matches the Swagger `info.description` |
| Host | Matches the Swagger `host` value |
| Base URL | Matches the Swagger `basePath` value |

Click **Security →** to continue.

---

### Step 4: Configure Security

Configure authentication based on your connector's requirements:

#### API Key Authentication
1. Select **API Key** as Authentication type
2. Set the parameter name (e.g., `X-API-Key`)
3. Set the parameter location (Header, Query, or Cookie)

#### OAuth 2.0 Authentication
1. Select **OAuth 2.0** as Authentication type
2. Select the Identity Provider (Generic OAuth 2 or specific provider)
3. Fill in:
   - Client ID
   - Client Secret
   - Authorization URL
   - Token URL
   - Refresh URL (if applicable)
   - Scope

#### No Authentication
1. Select **No authentication** if the API is public

Click **Definition →** to continue.

---

### Step 5: Review Definition

On the **Definition** tab:

1. Review all imported **Actions** (operations)
2. For each action, verify:
   - Summary and description are correct
   - Parameters are properly defined
   - Response schemas are accurate
3. Edit any actions that need adjustment by clicking the action name

Click **Code →** to continue (if available) or **Test →**.

---

### Step 6: Add Custom Code (Critical Step)

On the **Code** tab:

1. Toggle **Code Enabled** to **On**
2. In the code editor, **delete any existing code**
3. Open the connector's `script.csx` file
4. **Copy the entire contents** of the file
5. **Paste** into the code editor
6. Under **Operations**, select which operations should use the custom code
   - Typically, select **all operations** that have custom handling in the script

> ⚠️ **Important:** If the Code tab is not visible, you may need to:
> - Use a different environment
> - Check your license includes custom code support
> - Ensure you're using the new connector experience

Click **Test →** to continue.

---

### Step 7: Create Connector

Before testing, you must save the connector:

1. Click **Create connector** (or **Update connector** if editing)
2. Wait for the connector to be created/updated
3. Note any validation warnings or errors

---

### Step 8: Create a Connection

1. On the **Test** tab, click **+ New connection**
2. Enter any required credentials:
   - API Key (if using API key auth)
   - Sign in (if using OAuth)
3. Click **Create**
4. The new connection should appear in the Connections dropdown

---

### Step 9: Test Operations

For each operation:

1. Select the operation from the left panel
2. Fill in any required parameters
3. Click **Test operation**
4. Verify the response:
   - Status code is as expected
   - Response body is correct
   - Custom code transformations are working

---

## 🔄 Updating an Existing Connector

### Option 1: Update via UI

1. Navigate to **Data** → **Custom connectors**
2. Click the **⋯** menu on your connector
3. Select **Edit**
4. Make your changes
5. Click **Update connector**

### Option 2: Replace via Import

1. Navigate to **Data** → **Custom connectors**
2. Click the **⋯** menu on your connector
3. Select **Edit**
4. On the General tab, click **Import** (under the connector icon)
5. Upload the updated `apiDefinition.swagger.yaml`
6. Re-add the custom code from `script.csx`
7. Click **Update connector**

> ⚠️ **Warning:** Importing a new definition may reset some settings. Review all tabs before updating.

---

## 🔍 Troubleshooting

### Connector Won't Save

**Issue:** "Internal Server Error" when creating/updating

**Solutions:**
1. Check for compilation errors in your C# code
2. Verify only supported namespaces are used
3. Ensure script is under 1 MB
4. Check OpenAPI definition for syntax errors

---

### Code Tab Not Visible

**Issue:** The Code tab doesn't appear in the connector wizard

**Solutions:**
1. Ensure you're using a non-default environment
2. Verify your license supports custom code
3. Try creating the connector in a different environment
4. Contact your Power Platform administrator

---

### Operation Returns Unexpected Results

**Issue:** The custom code doesn't seem to be executing

**Solutions:**
1. Verify the operation is selected in the Code tab's operation list
2. Check the `OperationId` in your code matches the Swagger definition
3. Handle base64-encoded OperationId (see code template)
4. Test the operation and check the raw response

---

### Authentication Failures

**Issue:** 401 Unauthorized when testing

**Solutions:**
1. Verify API key/credentials are correct
2. Check the authentication header name matches the API's requirements
3. For OAuth, ensure redirect URI is configured correctly
4. Refresh the connection and try again

---

## 📤 Exporting a Connector

To export a connector for backup or sharing:

1. Navigate to **Data** → **Custom connectors**
2. Click the **⋯** menu on your connector
3. Select **Download**
4. Choose the format:
   - **OpenAPI definition** - Downloads the Swagger file
   - **Connector package** - Downloads a ZIP with all files

> **Note:** The exported OpenAPI file may differ from the original due to Power Platform modifications.

---

## 🌍 Deploying to Different Environments

### Development → Test → Production

1. Export the connector from Development
2. In the target environment:
   - Create new custom connector
   - Import the exported definition
   - Re-add the custom code
   - Configure security credentials
3. Test all operations

### Using Solutions (Recommended)

For ALM (Application Lifecycle Management):

1. Create a Solution in your environment
2. Add the custom connector to the Solution
3. Export the Solution
4. Import the Solution to target environment

> Solutions preserve connector settings but you may still need to reconfigure connections.

---

## 📋 Deployment Checklist

Before considering deployment complete:

- [ ] Connector created successfully (no errors)
- [ ] All operations visible in the connector
- [ ] Custom code enabled and added
- [ ] Security configured correctly
- [ ] Connection created successfully
- [ ] All operations tested and working
- [ ] Error responses handled appropriately
- [ ] Documentation updated if needed

---

## 🔗 References

- [Create a custom connector from an OpenAPI definition](https://learn.microsoft.com/en-us/connectors/custom-connectors/define-openapi-definition)
- [Create a custom connector from scratch](https://learn.microsoft.com/en-us/connectors/custom-connectors/define-blank)
- [Use custom code support](https://learn.microsoft.com/en-us/connectors/custom-connectors/write-code)
- [Share custom connectors](https://learn.microsoft.com/en-us/connectors/custom-connectors/share)
