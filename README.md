# DockerRegistrySync

This tool allows you to dump, restore and sync some registry keys into a json file.

This has been designed to "dockerize" applications that are using the registry to persist some data.

## Documentation
```
Usage: 
  DockerRegistrySync <json file path> <registry key path> [--sync]

Where:
  <json file path>    : Path of the json file to sync
  <registry key path> : Path of the registry key to sync
  --sync              : (Default) Restore the registry from the json file at startup, then subscribe to registry
                        changes and backup it to the json file on each change (cf. How it works).
  --restore           : Restores the registry from the json file and exit
  --backup            : Backups the registry to the json file and exit
  --full              : On restore, remove from the registry keys and value that are not present in the json file

Examples:
  DockerRegistrySync ""C:\DockerVolume\MyAppConfig.json"" ""HKEY_CURRENT_USER\Software\MyApp""
  DockerRegistrySync ""C:\DockerVolume\MyAppConfig.json"" ""HKEY_CURRENT_USER\Software\MyApp"" --sync
  DockerRegistrySync ""C:\DockerVolume\MyAppConfig.json"" ""HKEY_CURRENT_USER\Software\MyApp"" --sync --restore

How it works:
  This application will read the provided json file at startup, and will update the windows registry accordingly,
  then it will subscribe to registry changes and will update the json file each time a value of the registry is updated.
```

## Example

An example of its usage can be found in the [docker image of plex for windows](https://github.com/dr1rrb/docker-plex-win/blob/master/PlexSetup/Run.cmd)
