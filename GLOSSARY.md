# Tern

Tern is an open-source command-line tool for Sitecore. It is a drop-in replacement for the Official CLI, and it adds what that tool lacks: speed, output that agents can use, and everyday content operations.

## The tools

**Official CLI**:
Sitecore's own command-line tool and its plugins, which this project matches and replaces.
_Avoid_: Sitecore CLI (ambiguous once this tool exists), the old CLI

**Drop-in**:
A team can swap the Official CLI for this tool by changing only the tool reference, and every existing script, configuration file, item file and stored login keeps working without a single changed byte.
_Avoid_: compatible, replacement (unqualified)

**CLI compatibility**:
How closely this tool matches the Official CLI's files, commands, output and stored logins.
_Avoid_: compatibility (unqualified)

**Platform compatibility**:
Which Sitecore products and versions this tool works against.
_Avoid_: compatibility (unqualified), support matrix

**Plugin**:
One of the Official CLI's add-on packages listed in `sitecore.json`. This tool never loads them.
_Avoid_: using "plugin" for this tool's own add-ons

**Extension**:
A separately installed program that adds a command to this tool.
_Avoid_: plugin

## Where commands run

**SitecoreAI**:
Sitecore's SaaS content platform, formerly named XM Cloud.
_Avoid_: XM Cloud (former name), XMC

**On-prem**:
Sitecore XM or XP 10.x, installed and run by the customer.
_Avoid_: XP (when XM is also meant), self-hosted

**CM**:
The content-management instance a command reads from or writes to.
_Avoid_: instance, server, "Sitecore" as a noun for the instance

**Local CM**:
A CM running in containers on a developer's machine.
_Avoid_: Docker (as a noun for the CM), local Sitecore

**Management Services**:
The Sitecore server module on a CM that the Official CLI talks to.
_Avoid_: SMS (in prose), management API

**Deploy API**:
SitecoreAI's service for organisations, projects, environments and deployments.
_Avoid_: control plane, cloud API

**Environment**:
One deployed SitecoreAI CM within a project, such as dev or prod.
_Avoid_: endpoint, instance

**Endpoint**:
A named, stored login to a CM or to the Deploy API.
_Avoid_: environment (even though the Official CLI's option is called `--environment-name`), connection, profile

## Serialization

**Module**:
A named set of serialization rules that says which items are serialized and where their files live.
_Avoid_: package, project

**Include**:
A rule in a Module that names one subtree of items to serialize.
_Avoid_: path, root

**Item file**:
The on-disk form of one Sitecore item.
_Avoid_: yml, YAML file, serialized item

**Pull**:
Making the item files match the CM.
_Avoid_: sync, export, download

**Push**:
Making the CM match the item files.
_Avoid_: sync, import, deploy

**Watch**:
A continuous Pull that follows changes on the CM as they happen.
_Avoid_: sync, live sync

**Push operations**:
The limit on what a Push may do within an Include: create only; create and update; or create, update and delete.
_Avoid_: permissions, mode

**Orphan**:
An item that exists on only one side of a Pull or Push within an Include: a file whose item is gone, or an item with no file.
_Avoid_: stale item, leftover

**Signature**:
The fingerprint of an item's content that decides whether it has changed.
_Avoid_: hash, checksum, revision

**Change history**:
The CM's short-lived record of which items changed recently and when.
_Avoid_: history (unqualified), audit log, event queue

## Changing things safely

**Plan**:
The exact list of item changes a Pull or Push will make, worked out before anything changes, together with the Signatures it expects the target to still have.
_Avoid_: diff, changeset, what-if output

**Apply**:
Carrying out a saved Plan. It is refused if the target no longer matches what the Plan expected.
_Avoid_: execute, run, deploy

**Deviation**:
A deliberate difference from the Official CLI, allowed only where this tool succeeds and the Official CLI fails.
_Avoid_: bug fix, improvement, incompatibility

**Accelerator**:
An optional server module that makes this tool faster on any CM where it is installed.
_Avoid_: server plugin, agent
