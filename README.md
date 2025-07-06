# DEPRECATION NOTICE
**This tool has been superseeded by the [Tools](https://github.com/AlbumShuffler/Tools) repository.**

## Album Shuffler
This repository contains a self-contained dotnet application to automatically retrieve data from the Spotify API and save it as local JSON files.

Using this tool does not require the dotnet sdk or runtime! Download the latest release (pre-built for Linux, Mac & Windows) and run it. The downloads contain self-contained binaries meaning they do not have any external dependencies.

### Usage

#### Spotify Access
In order to retrieve data from the Spotify Web API you need a Spotify account. Go to [Spotify for Developers](https://developer.spotify.com/), login and create a new app. This will give you a `client_id` and a `client_secret`. These two values allow you to create an access token that needs to be send with every request to the api. This project does not need access to user or user profile data.

#### Input File
To tell the scripts what artists or playlists to download you need to supply an input file. It needs to be a JSON file in the following form:
```
[
  {
    "shortName": "Short 1",
    "httpFriendlyShortName": "short1",
    "type": "artist",
    "id": "abcdefgh",
    "icon": "img/short_1.png",
    "coverCenterX": 50,
    "coverCenterY": 50,
    "coverColorA": "#112233",
    "coverColorB": "#556677"
  },
  {
    "shortName": "Short 2",
    "httpFriendlyShortName": "short2",
    "type": "playlist",
    "id": "ijklmnop",
    "icon": "img/short_2.png",
    "coverCenterX": 50,
    "coverCenterY": 75,
    "coverColorA": "#334455",
    "coverColorB": "#778899"
  }
]
```
Here is a breakdown of the properties:
```
shortName: Short name of the artist, is used at some places where space is tight
httpFriendlyShortName: Acts as an identifier of sorts; Needs to be unique and formatted in a way that it works nicely in HTML/JS settings
type: Is either "artist" or "playlist"; tells the script which endpoint to use to download metadata
id: Id if the artist or playlist on Spotify; You can find these by looking at the urls when opening the artist/playlist in the web interface
icon: Link to a small icon for this artist/playlist
coverCenterX/Y: The covers are zoomed in by the web app; This tells the web app which point to center; 50,50 is the center
coverColorA/B: The covers have light glow effect on the top right und bottom left corder
```

#### Update data
To generate new album data run the following command:
```
./AlbumShuffler.DataRetriever $SPOTIFY_CLIENT_ID $SPOTIFY_CLIENT_SECRET $INPUT_FILE $OUTPUT_DIR
```
Running the app will remove the given output folder and recreate it to make sure its empty.
