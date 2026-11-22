# Releasing

How a version of the SDKs is released. This is for the maintainers.

All six SDKs share one version number, taken from the release tag (`v1.2.3`). Where each one goes:

| SDK | Where a release goes | Name |
|---|---|---|
| Swift | Nowhere: Swift Package Manager reads the tag from this repository | `erdigo-sdk`, product `ErdigoSDK` |
| .NET | [nuget.org](https://www.nuget.org/packages/Erdigo.Sdk) | `Erdigo.Sdk`, `Erdigo.Core`, `Erdigo.MockReceiver`, and the tool `Erdigo.Send` |
| Python | [PyPI](https://pypi.org/project/erdigo-sdk/) | `erdigo-sdk` |
| Node.js | [npm](https://www.npmjs.com/package/erdigo-sdk) | `erdigo-sdk` |
| Java | Not published | |
| PHP | Not published | |

## Versions

Follow [semantic versioning](https://semver.org/): a change that can break a program using the SDK raises the first
number, a new feature the second, a fix the third. The protocol has its own version (integration-v1), which a release of
the SDKs does not change.

The .NET packages depend on exactly the same version of each other, because they share internal code.

## Making a release

1. In a pull request, change the version everywhere it is written. The **Publish** workflow checks these and refuses a
   tag that does not match them:
   - `python/pyproject.toml`, `python/src/erdigo_sdk/__init__.py`, `python/src/erdigo_sdk/pairing.py`,
     `python/tests/test_pairing.py`
   - `nodejs/package.json`, `nodejs/src/pairing.js`
   - `dotnet/Directory.Build.props`, `dotnet/src/Erdigo.Sdk/Pairing.cs`
   - `swift/Sources/ErdigoSDK/Pairing.swift`
   - `java/pom.xml`, `java/src/main/java/com/ziorentia/erdigo/sdk/Pairing.java`
   - `php/src/Pairing.php`

   Then, in the same pull request:
   - Update the .NET lock files, which record the version: in `dotnet`, run
     `dotnet restore Erdigo.sln --force-evaluate`.
   - For a new first number, change `from: "1.0.0"` in `swift/README.md`.
2. Merge it once its checks pass.
3. Create the release on GitHub with a new tag on that commit of `main`, such as `v1.2.3`, and say what changed. Do not
   mark it as a pre-release; the workflow only publishes tags like `v1.2.3`.
4. Publishing the release starts **Publish**, which builds the packages from the tag and puts them on each registry that
   is set up. Check that the publishing job of every registry that is set up ran (a registry that is not set up is
   skipped, and the run still passes), and that the version shows on each registry's page.

A tag cannot be moved once packages are published from it, and a registry never takes the same version twice. To fix
a release, make a new one.

To publish a release again, for example after a registry had a problem, run the workflow on the tag. A version that is
already on a registry is skipped.

```bash
gh workflow run publish.yml --ref v1.2.3
```

## Setting up the registries

Nothing in this repository holds a password or an API key. Each registry is told to trust the **Publish** workflow
(trusted publishing), and the workflow proves who it is with a short-lived token from GitHub.

Turn on two-factor authentication for every registry account, and keep at least two owners where the registry allows
it.

### The environments

Each publishing job runs in a GitHub environment named after its registry: `pypi`, `npm` and `nuget`. Before any
registry is set up, create the three under **Settings → Environments**, and for each set **Deployment branches and
tags** to **Selected branches and tags** with only the tag rule `v*`. Then only a release tag can use them, and the
registries trust only those environments.

A registry is used only once its repository variable is `true`, under **Settings → Secrets and variables → Actions →
Variables**: `PUBLISH_PYPI`, `PUBLISH_NPM` or `PUBLISH_NUGET`. Until then the workflow skips it.

### PyPI

Under the account's **Publishing** settings, add a trusted publisher for a new project (a "pending publisher"): project
`erdigo-sdk`, owner `Ziorentia`, repository `Erdigo-sdk`, workflow `publish.yml`, environment `pypi`. Then set
`PUBLISH_PYPI` to `true`. A pending publisher does not reserve the name, so make the release soon after.

### nuget.org

On the account's **Trusted Publishing** page, add a policy: owner `Ziorentia`, repository `Erdigo-sdk`, workflow
`publish.yml`, environment `nuget`, allowed to push new packages and new versions of `Erdigo.*`. Set the repository
variable `NUGET_USER` to the nuget.org account name, and `PUBLISH_NUGET` to `true`.

### npm

npm can trust a workflow only for a package that already exists, so its first version is published by hand, by an
owner of the package, right after the release is made: from a clean clone at the release tag,

```bash
npm login
npm publish ./nodejs --access public
```

The `./` matters: without it npm reads `nodejs` as the name of a GitHub repository. npm may first show a placeholder
version, `0.0.0-stage`, as the package's latest version while it checks the upload; the real version replaced it as
latest about a minute later for 1.0.0. Check the package's page, and approve the version under **Staged Packages** in
the account settings if it is waiting there.

npm also lets a new trusted publisher wait at most two days for its first publish, after which it expires and must be
deleted and added again. So add it only when the next release is about to be made: in the package's settings on
npmjs.com, add a trusted publisher for GitHub Actions with organization or user `Ziorentia`, repository `Erdigo-sdk`,
workflow `publish.yml` and environment `npm`, and under **Allowed actions** also allow `npm publish`. Then set
`PUBLISH_NPM` to `true` and make the release within two days. Once that works, set the package to require two-factor
authentication and to disallow tokens.

### A registry for the first time

Until a package is on its registry, the install command in its README does not work, and its name could be taken by
someone else. So set the registry up first, merge the version change, create the release at once, and check the
package's page.
