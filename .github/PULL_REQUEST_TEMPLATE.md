## What changes and why

<!-- One subject per pull request. If it closes an issue, write "Closes #123". -->

## How it was tested

<!-- The commands you ran, and the requests you made by hand. -->

## Checklist

- [ ] `dotnet build AlgoJudge.sln -warnaserror` and `dotnet test AlgoJudge.sln` pass. The tests need Docker running.
- [ ] If the API changed, `openapi.json` is taken from the running container, as the README describes.
- [ ] If an error code was added or renamed, `error-codes.json` lists it.
- [ ] A schema change comes with a new migration. Existing migrations are not edited.
- [ ] No secrets, credentials, or `.env` files are committed.
