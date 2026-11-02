# CK model snapshots for tests

`ck-system.identity-3.0.0.yaml` is the **compiler output** of the `System.Identity` CK model of
octo-identity-services (`src/Persistence.IdentityCkModel/ConstructionKit`, version 3.0.0, `ckLanguage: 2`).
The asset repository does not ship System.Identity; the credential guard tests (`Security/IdentityCredentialGuard*Tests`)
load this file as an embedded model (`Fixtures/SystemIdentitySnapshotCkModel`) so they run against the real access
matrix of the user instead of a test model.

The file is byte-identical to what the compiler writes. Do not edit it by hand.

## Refresh

When System.Identity changes the access of a user attribute (for example when an attribute becomes `MethodOnly`), refresh
the snapshot and enable the skipped tests whose `Skip` reason names the change (the work item of the fix):

```bash
# 1. export the model sources of octo-identity-services origin/main into a scratch folder
git -C ../octo-identity-services archive origin/main src/Persistence.IdentityCkModel/ConstructionKit | tar -x -C <scratch>

# 2. compile them with octo-ckc (a DebugL build of octo-construction-kit-engine, a catalog that holds the System version
#    the model depends on)
dotnet <engine>/bin/DebugL/net10.0/publish/octo-ckc.dll -c compile \
  -p <scratch>/src/Persistence.IdentityCkModel/ConstructionKit -o <scratch>/out -lce true -lcr <catalog root>

# 3. copy <scratch>/out/ck-system.identity-3.yaml to ck-system.identity-<version>.yaml here and update
#    SystemIdentitySnapshotCkModel.ModelFullName and the csproj item if the file name changed
```

## `ck-system.identity-2.30.0.yaml`

The compiler output of the last System.Identity 2.x release (octo-identity-services `origin/main` before AB#8053, same procedure as
above with the 2.30.0 sources). It is **not** an embedded model of the test host; `Tenant/IdentityMajorUpgradeFlowTests` imports it
explicitly into a tenant and then upgrades that tenant to the embedded 3.0.0 snapshot (existing tenants must upgrade cleanly). Do not
refresh it: it documents the old major.
