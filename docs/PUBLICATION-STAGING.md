# Fresh source publication procedure

This procedure prepares a new source-only history after separate approval. It never connects the canonical repository's existing history to the old GitHub repository.

## Prepared local source tree

Inside the canonical project, the review tree is:

`.local/fresh-publication-20261023/source`

It is a non-destructive copy of the explicitly selected current source and documents. It is not a clone or Git worktree. It has no .git directory, commits or staged entries. The canonical index, refs and history remain unchanged.

PUBLISH-FILES.txt enumerates the exact proposed files. SOURCE_MANIFEST.json hashes all reviewed files except itself and PUBLISH-FILES.txt to avoid self-reference; the path list includes both inventory files. Recheck hashes immediately before approval.

## Repository status

A read-only check on 23 October 2026 found the existing [cammaxwel-droid/Toolbox-Matrix](https://github.com/cammaxwel-droid/Toolbox-Matrix) repository with Private visibility and no branches returned. The earlier preparation attempt did not create it. Do not create a duplicate or replace this repository.

Immediately before any separately approved push, recheck the account, repository, Private visibility and empty state. If it contains commits, stop and inspect them before proceeding. Leave the older FP2.4 repository and the canonical origin remote untouched.

## Attribution-corrected binary candidate

The separately checked binary package is `preview/V1-Public-Candidates/1.0.0-candidate-sanitized-20261023-attribution-corrected.zip` in the canonical project. It identifies Cameron Maxwell in the project notices and retains the existing application payload. The original `1.0.0-candidate-sanitized-20261023` folder and ZIP remain untouched.

This ZIP contains the compiled application and notices. The initial source publication still uses the 66 files in PUBLISH-FILES.txt; do not substitute the binary ZIP for that source set. The source-code licence remains undecided, and public distribution is not approved.

## Later commit and private push — separate explicit approval required

These commands have not been executed. Run them only after the owner has approved the exact reviewed source snapshot and the empty private destination. Recheck PUBLISH-FILES.txt and manifest hashes first. A private push can retain the current reserved-rights notice; public release requires a separate licence decision.

From the prepared source tree:

```powershell
# Confirm this is the prepared copy, never the canonical repository root.
if ((Test-Path -LiteralPath '.git') -or
    !(Test-Path -LiteralPath 'SOURCE_MANIFEST.json') -or
    !(Test-Path -LiteralPath 'PUBLISH-FILES.txt')) {
    throw 'Unexpected publishing directory; stop and inspect.'
}

# Run only after separate approval to initialise, stage, commit and push.
git init -b main
git config user.name 'cammaxwel-droid'
$gitNoReply = Read-Host 'Paste your GitHub noreply email from GitHub Settings > Emails'
if ($gitNoReply -notmatch '^[^ @]+@users[.]noreply[.]github[.]com$') {
    throw 'Use your verified GitHub noreply address before committing.'
}
git config user.email $gitNoReply
git config core.autocrlf false
$files = @(Get-Content -LiteralPath 'PUBLISH-FILES.txt' | Where-Object { $_ })
git add -- $files
git diff --cached --check
git diff --cached --stat
git diff --cached

# Review staged contents before continuing.
git commit -m 'Prepare Toolbox Matrix V1 source candidate'
git remote add origin https://github.com/cammaxwel-droid/Toolbox-Matrix.git
git push -u origin main
```

The private noreply address above avoids publishing a personal email; the owner may substitute their verified GitHub noreply address. Do not use the canonical root's .git, force-push, merge unrelated histories, push all refs, create a tag/release or change visibility.

If the destination has acquired commits, stop and reconcile that fact with the owner. Do not overwrite it. If authentication requires a browser or prompt, the user must complete that interaction.

## Exclusions and housekeeping diagnosis

The six inactive third-party product logos and their SOURCES.md are excluded pending rights verification. Local originals and the original approved icon remain untouched.

Build-Local.ps1, Build-V1Candidate.ps1, Review-InterfaceConsistency.ps1 and Verify-TargetedCli.ps1 are retained in the canonical folder but excluded from this fresh source proposal. The new headless procedure is sufficient to build and check the publication source set. The historical verifier expects deleted `preview/Toolbox-Matrix-FP2.4-Refinement/build/WinGetToolbox.dll` and `docs/FP0.5.0-TARGETED-VERIFICATION.json`, plus a historical six-launcher inventory. It is not valid current verification and has not been run or silently repaired.

Seven earlier tracked document deletions remain unstaged in the canonical repository. No old file is restored or deleted to make this fresh source snapshot.

## Public release

Keep the repository private until the licence, provenance and human gates in [RELEASE-CHECKLIST](RELEASE-CHECKLIST.md) have been resolved and public visibility is explicitly approved. Neither a clean source set nor a private push approves a binary release.
