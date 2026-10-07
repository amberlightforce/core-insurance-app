#!/usr/bin/env python3
"""Least-privilege guard for Key Vault access in the compiled Bicep (ARM JSON) of infra/azure/main.bicep.

Usage:  bicep build infra/azure/main.bicep --outfile main.json   (or: az bicep build ... --outfile main.json)
        python3 scripts/check-keyvault-rbac.py main.json

Fails (exit 1) when
  * any role assignment anywhere is scoped to the Key Vault itself instead of to a single secret,
  * a Key Vault role is assigned outside modules/keyvault.bicep,
  * a secret is readable by an identity that is not on its allow-list (SECRET_READERS below),
  * an app or job references a Key Vault secret it may not use, or uses another workload's identity for it.
"""
import json
import re
import sys

KEY_VAULT_ROLE_IDS = {
    "4633458b-17de-408a-b874-0445c86b69e6",  # Key Vault Secrets User
    "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",  # Key Vault Secrets Officer
    "00482a5a-887f-4fb3-b363-3b7fe8e74483",  # Key Vault Administrator
    "21090545-7ca7-4776-b22c-e363652d74d2",  # Key Vault Reader
}

# secret name -> identities (identities.bicep outputs) allowed to read it
SECRET_READERS = {
    "connectionstrings-core": {"api", "worker"},
    "entra-client-secret": {"api"},
    "connectionstrings-migrator": {"migrate"},
    "connectionstrings-admin": {"bootstrap"},
    "db-app-password": {"bootstrap"},
    "db-migrator-password": {"bootstrap"},
}

# keyvault.bicep output -> secret name
SECRET_OUTPUTS = {
    "coreSecretUri": "connectionstrings-core",
    "entraSecretUri": "entra-client-secret",
    "migratorSecretUri": "connectionstrings-migrator",
    "adminSecretUri": "connectionstrings-admin",
    "appRoleSecretUri": "db-app-password",
    "migratorRoleSecretUri": "db-migrator-password",
}

# app/job module deployment -> its own identity
WORKLOAD_IDENTITY = {
    "app-api": "api",
    "app-worker": "worker",
    "app-gotenberg": "gotenberg",
    "job-migrate": "migrate",
    "job-bootstrap": "bootstrap",
}

IDENTITY_REF = re.compile(r"outputs\.(\w+)\.value\.(?:principalId|id|clientId)")


def resources(template):
    items = template.get("resources", [])
    return list(items.values()) if isinstance(items, dict) else list(items)


def deployments(template):
    return {r["name"]: r for r in resources(template) if r.get("type") == "Microsoft.Resources/deployments"}


def walk_role_assignments(template, path):
    for resource in resources(template):
        if resource.get("type") == "Microsoft.Authorization/roleAssignments":
            yield path, resource
        if resource.get("type") == "Microsoft.Resources/deployments":
            yield from walk_role_assignments(resource["properties"]["template"], path + [resource["name"]])


def main(compiled_path):
    template = json.load(open(compiled_path, encoding="utf-8"))
    errors = []
    modules = deployments(template)

    # 1. Every role assignment: Key Vault roles only in the keyvault module, and only on single secrets.
    secret_params = {}
    for path, assignment in walk_role_assignments(template, []):
        role = assignment["properties"]["roleDefinitionId"]
        scope = assignment.get("scope", "")
        is_kv_role = any(role_id in role for role_id in KEY_VAULT_ROLE_IDS) or "secretsUserRoleId" in role
        if "Microsoft.KeyVault/vaults'" in scope or re.search(r"Microsoft\.KeyVault/vaults'\s*,\s*parameters\('name'\)\)", scope):
            errors.append(f"{'/'.join(path)}: role assignment at Key Vault scope ({scope})")
        if not is_kv_role:
            continue
        if path != ["keyvault"]:
            errors.append(f"{'/'.join(path) or 'main'}: Key Vault role assigned outside modules/keyvault.bicep")
            continue
        secret = re.search(r"Microsoft\.KeyVault/vaults/secrets', parameters\('name'\), '([^']+)'", scope)
        if not secret:
            errors.append(f"keyvault: Key Vault role not scoped to a single secret ({scope or 'resource group'})")
            continue
        param = re.search(r"parameters\('(\w+)'\)\[copyIndex\(\)\]", assignment["properties"]["principalId"])
        if not param:
            errors.append(f"keyvault: principal of {secret.group(1)} is not a reader parameter")
            continue
        secret_params.setdefault(secret.group(1), set()).add(param.group(1))

    # 2. Resolve reader parameters to identities and compare with the allow-list.
    keyvault_params = modules["keyvault"]["properties"]["parameters"]
    for secret, allowed in SECRET_READERS.items():
        if secret not in secret_params:
            errors.append(f"keyvault: no reader assignment for {secret}")
            continue
        readers = set()
        for param in secret_params[secret]:
            readers |= set(IDENTITY_REF.findall(json.dumps(keyvault_params[param]["value"])))
        if not readers <= allowed:
            errors.append(f"keyvault: {secret} readable by {sorted(readers - allowed)} (allowed: {sorted(allowed)})")
    for secret in secret_params.keys() - SECRET_READERS.keys():
        errors.append(f"keyvault: unexpected secret with readers: {secret}")

    # 3. Apps and jobs: Key Vault references only to their own secrets, with their own identity.
    for module_name, identity in WORKLOAD_IDENTITY.items():
        module = modules.get(module_name)
        if module is None:
            errors.append(f"module {module_name} not found")
            continue
        params = module["properties"]["parameters"]
        own = set(IDENTITY_REF.findall(json.dumps(params["identityId"]["value"])))
        if own != {identity}:
            errors.append(f"{module_name}: runs as {sorted(own)}, expected {identity}")
        for entry in params.get("secrets", {}).get("value", []):
            output = re.search(r"outputs\.(\w+)\.value", entry.get("keyVaultUrl", ""))
            secret = SECRET_OUTPUTS.get(output.group(1)) if output else None
            if secret is None:
                errors.append(f"{module_name}: secret {entry.get('name')} is not a known Key Vault secret")
                continue
            if identity not in SECRET_READERS[secret]:
                errors.append(f"{module_name}: references {secret}, which {identity} may not read")
            used = set(IDENTITY_REF.findall(entry.get("identity", "")))
            if used != {identity}:
                errors.append(f"{module_name}: reads {secret} with identity {sorted(used)}, expected {identity}")

    if errors:
        print("Key Vault least-privilege check FAILED:")
        for error in errors:
            print(f"  - {error}")
        return 1
    print(f"Key Vault least-privilege check passed: {len(SECRET_READERS)} secrets, per-secret scopes only.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "main.json"))
