# Birooni Licensing & Revit Addin Infrastructure

End-to-end software licensing, hardware fingerprinting, and cryptographic validation infrastructure for **Birooni**, an Autodesk Revit plugin.

---

## Architecture Overview

```
                               ┌────────────────────────────────────────┐
                               │        PostgreSQL on Supabase          │
                               │  Session Pooler (Port 5432, IPv4)      │
                               │  - licenses                            │
                               │  - activations                         │
                               │  - validation_logs (TIMESTAMPTZ)       │
                               └──────────────────▲─────────────────────┘
                                                  │
                                                  │ Npgsql / EF Core
                                                  │
┌──────────────────────────────────────┐       ┌──┴─────────────────────────────────────┐
│       Autodesk Revit Addin           │       │    ASP.NET Core 8 Web API              │
│       ('Birooni.Client')             │       │    ('Birooni.Licensing.Api')           │
│                                      │       │    Hosted on Render (Docker container) │
│ - Composite Hardware Fingerprinting  │ HTTP  │                                        │
│ - Offline Cache (%ProgramData%)      ├───────► - RSA-2048 SHA-256 PKCS#1 Signer       │
│ - Non-blocking OnStartup check       │       │ - Trial Fraud Prevention               │
│ - Background Server Ping Revalidation│       │ - Max Activation Seat Limits           │
└──────────────────────────────────────┘       └────────────────────────────────────────┘
```

---

## 1. Backend Web API (`Birooni.Licensing.Api`)

### Endpoints
| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/license/activate` | Activates license on hardware device; checks seat limits (`MaxActivations`). |
| `POST` | `/api/license/validate` | Revalidates device activation and issues refreshed 14-day signed token. |
| `POST` | `/api/license/deactivate` | Deactivates device and releases seat. |
| `POST` | `/api/license/trial` | Issues 14-day trial; blocks duplicate trials for the same `DeviceId`. |
| `GET`  | `/api/license/public-key` | Serves RSA public key in PEM format for client offline verification. |
| `GET`  | `/swagger` | Interactive Swagger UI API documentation. |

---

## 2. Autodesk Revit Client Library (`Birooni.Client`)

A standalone C# class library designed to be referenced directly by Revit addins (.NET 8 for Revit 2025/2026).

### Features
1. **Hardware Fingerprint Collector**:
   - Produces a deterministic SHA-256 hash formatted as `HW-<HEX>` using:
     - Motherboard Serial Number (`Win32_BaseBoard.SerialNumber` via WMI)
     - CPU Processor ID (`Win32_Processor.ProcessorId` via WMI)
     - Windows Machine GUID (`HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`)
   - Includes graceful fallbacks (Network Interface MAC + Machine Name + Processor Count) if WMI is restricted by enterprise group policies.
2. **Offline RSA Token Cache**:
   - Stores signed token at `%ProgramData%\Birooni\license.lic`.
   - Validates digital signature using the embedded RSA-2048 public key.
   - Enforces device binding: prevents copying the `.lic` file to another computer.
   - Enforces 14-day lease window (`ValidUntil`) and perpetual/term expiration (`ExpiresAt`).
3. **Non-Blocking Revit Addin Startup**:
   - `InitializeLicenseAsync()` inspects `%ProgramData%` and grants immediate UI access in milliseconds without freezing Revit's UI thread.
   - Concurrently spawns a background ping to `/api/license/validate` to refresh the cached token.

### Revit Addin Integration Example (`IExternalApplication`)

```csharp
using Autodesk.Revit.UI;
using Birooni.Client;
using Birooni.Client.Models;

public class BirooniApp : IExternalApplication
{
    private BirooniLicenseClient _licensing;

    public Result OnStartup(UIControlledApplication application)
    {
        // 1. Initialize license client with API URL and Public Key PEM
        string apiUrl = "https://birooni-api.onrender.com";
        string publicKeyPem = @"-----BEGIN PUBLIC KEY-----
...YOUR_RSA_PUBLIC_KEY_PEM...
-----END PUBLIC KEY-----";

        _licensing = new BirooniLicenseClient(apiUrl, publicKeyPem, pluginVersion: "2026.1.0");

        // 2. Listen for background revalidation updates
        _licensing.BackgroundValidationCompleted += (sender, result) =>
        {
            if (!result.IsGranted)
            {
                // Optionally disable ribbon buttons or show notification
            }
        };

        // 3. Fast offline check (non-blocking)
        var initResult = _licensing.InitializeLicenseAsync().GetAwaiter().GetResult();

        if (initResult.IsGranted)
        {
            // Build Revit ribbon buttons and tabs immediately
            CreateRevitRibbon(application);
            return Result.Succeeded;
        }
        else
        {
            // Prompt user for activation or trial dialog
            ShowActivationDialog(application);
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }
}
```

---

## 3. Configuration

### Local Development (`appsettings.Development.json`)
Paste your Supabase connection string:
```json
{
  "ConnectionStrings": {
    "Database": "postgresql://postgres.cjragmpctpyvuntejbdb:YOUR_PASSWORD@aws-0-ap-southeast-1.pooler.supabase.com:5432/postgres"
  }
}
```
*Note: The URI parser handles both `postgres://` and `postgresql://` formats, including raw passwords with `@` and special characters.*

---

## 4. Production Deployment on Render

### Step 1: Push Repository to GitHub / GitLab
Ensure `Dockerfile` and `render.yaml` are at the repository root.

### Step 2: Create Web Service on Render
1. In Render Dashboard, click **New +** → **Web Service**.
2. Connect your Git repository.
3. Select **Docker** environment (or Render will detect the root `Dockerfile` automatically).
4. Choose **Free** plan instance.

### Step 3: Configure Environment Variables in Render Dashboard
Go to **Environment** tab in your Render Web Service and add:

| Key | Value | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Sets production environment |
| `DATABASE_URL` | `postgresql://postgres.cjragmpctpyvuntejbdb:YOUR_PASSWORD@aws-0-ap-southeast-1.pooler.supabase.com:5432/postgres` | Your Supabase Session Pooler connection string |
| `RSA_PRIVATE_KEY` | *(Optional)* `-----BEGIN RSA PRIVATE KEY-----...` | Persistent RSA private key PEM (if omitted, a transient 2048-bit key is generated) |

> [!NOTE]
> The `Dockerfile` automatically binds ASP.NET Core to Render's dynamic `$PORT` environment variable (`--urls http://0.0.0.0:${PORT:-8080}`).

---

## 5. Automated Tests

Run the test suite across the solution:
```bash
dotnet test Birooni.Licensing.sln
```
All 19 unit tests verify:
- Connection string & URI parsing (`postgres://`, `postgresql://`, query params, unescaped credentials)
- RSA TokenSigner cryptographic generation, tampering rejection, and 14-day validity
- Licensing service activation limits, validation, seat release, and trial duplicate prevention
- Hardware fingerprint collector deterministic hashing and WMI fallbacks
- Offline token caching and hardware binding verification
