open System
open FsToolkit.ErrorHandling

open System.IO
open System.Linq
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Spectre.Console
open SpotifyAPI.Web

type CommandLineArguments = {
    ClientId: string
    ClientSecret: string
    InputFilePath: string
    OutputPath: string
}

/// <summary>
/// Contains all metadata required to retrieve data from spotify and generate code for the frontend in a later step
/// </summary>
type Input = {
    ShortName: string
    HttpFriendlyShortName: string
    Type: string
    Id: string
    Icon: string
    CoverCenterX: int option
    CoverCenterY: int option
    AltCoverCenterX: int option
    AltCoverCenterY: int option
    IgnoreIds: string list option
    IgnoreShowWithStrings: string list option
    CoverColorA: string
    CoverColorB: string
}

type Image = {
    Url: string
    Height: int
    Width: int
}

/// <summary>
/// Contains all metadata required to create elm code in a later step. Most of the fields are not used in this application
/// </summary>
type ArtistOutput = {
    ShortName: string
    HttpFriendlyShortName: string
    Type: string
    Id: string
    Icon: string
    CoverCenterX: int option
    CoverCenterY: int option
    AltCoverCenterX: int option
    AltCoverCenterY: int option
    CoverColorA: string
    CoverColorB: string
    Name: string
    Images: Image list
}

/// <summary>
/// This represents a single playable item that will later be displayed.
/// Can be constructed from show episodes, albums and playlist entries
/// </summary>
type ItemOutput = {
    Id: string
    Name: string
    UrlToOpen: string
    Images: Image list
}

/// <summary>
/// Combines
/// </summary>
type Output = {
    Artist: ArtistOutput
    Albums: ItemOutput list
}


let (|?|) = defaultArg


/// <summary>
/// This makes System.Text.Json ignore case when deserializing and make it interpret `null` as `None` for option types
/// </summary>
let jsonSerializerOptions =
        let options =
            JsonFSharpOptions.Default()
                .WithSkippableOptionFields()
                .WithAllowNullFields()
                .ToJsonSerializerOptions()
        do options.PropertyNameCaseInsensitive <- true
        options


/// <summary>
/// Runs the given action.
/// If it encounters a `APITooManyRequestException` it will wait for the suggested time and try again.
/// Will abort of 20 consecutive tries fail
/// </summary>
let performRateLimitAwareRequest<'a> (action: unit -> Task<'a>) : Task<Result<'a, string>> =
    let maxRetryCount = 20
    let rec step (counter: int) =
        task {
            if counter <= maxRetryCount then
                try
                    let! result = action ()
                    return Ok result
                with
                | :? APITooManyRequestsException as rateLimitException ->
                    do printfn $"Got a rate limit response from the api, suggest retry in %A{rateLimitException.RetryAfter.TotalSeconds} seconds"
                    do! Task.Delay(rateLimitException.RetryAfter.Add(TimeSpan.FromSeconds(2)))
                    return! step (counter + 1)
                | exn ->
                    return Error $"Spotify api request failed because: %s{exn.Message}"
            else
                return Error $"Could not get data from api. Rate limit exceeded the retry counter of %i{maxRetryCount}"
        }
    step 1


let parseCommandLineArgs (args: string[]) : Result<CommandLineArguments, string> =
    match args with
    | [| clientId; clientSecret; inputFilePath; outputPath |] -> 
        Ok { 
            ClientId = clientId
            ClientSecret = clientSecret
            InputFilePath = inputFilePath
            OutputPath = outputPath
        }
    | _ ->
        Error "Required arguments: <client-id> <client-secret> <input-file-path> <output-path>"


let readFileContent (filePath: string) : Result<string, string> =
    try
        if File.Exists(filePath) then
            Ok(File.ReadAllText(filePath))
        else
            Error $"File not found: {filePath}"
    with
    | ex -> Error $"Error reading file: {ex.Message}"


let parseInput (json: string) : Result<Input list, string> =
    try
        let records = JsonSerializer.Deserialize<Input list>(json, jsonSerializerOptions)
        if obj.ReferenceEquals(records, null) then Error "Deserialization returned null."
        else Ok records
    with ex ->
        Error $"Failed to parse the input file: {ex.Message}"        
        
        
let createSpotifyClient clientId clientSecret : TaskResult<SpotifyClient, string> =
    taskResult {
        try
            let config = SpotifyClientConfig.CreateDefault()
            let request = ClientCredentialsRequest(clientId, clientSecret)
            let! response = OAuthClient(config).RequestToken(request)
            let spotify = SpotifyClient(config.WithToken(response.AccessToken))
            return spotify
        with ex ->
            return! Error $"Authenticating with Spotify failed because: {ex.Message}"
    }


let filterAlbums (idsToIgnore: string list) (showsToIgnore: string list) (albums: ItemOutput list) =
    albums
    |> List.where (fun album ->
            (idsToIgnore |> List.contains album.Id = false) &&
            (not <| (showsToIgnore |> List.exists (fun toIgnore -> album.Name.Contains(toIgnore))))
        )


let getAlbumsForArtist (client: SpotifyClient) (artistId: string) =
    taskResult {
        let! firstPageOfAlbums = performRateLimitAwareRequest (fun () -> artistId |> client.Artists.GetAlbums)
        let! allAlbums = firstPageOfAlbums |> client.PaginateAll |> Task.map List.ofSeq
        return allAlbums
    }


let getAlbumsFromPlaylist (client: SpotifyClient) (playlist: FullPlaylist) =
    taskResult {
        try
            let! allTracks = client.PaginateAll(playlist.Tracks) |> Task.map List.ofSeq
            let playlistTracks =
                    allTracks
                    |> List.map (fun element ->
                        match element.Track with
                        | :? FullTrack as track -> Some track
                        | :? FullEpisode -> failwith "Found an episode in a playlist. This might be valid but is not supported currently"
                        | other -> failwith $"Found an unknown type if IPlayableItem: ${other.GetType().FullName}")
                    |> List.choose id

            let albums = playlistTracks |> List.map (_.Album) |> List.distinctBy (_.Id)
            return albums
         with ex ->
             return! Error $"Could not get albums for all tracks of playlist ${playlist.Id} because: {ex.Message}"
    }


let knownInputTypes =
    [
    "artist"
    "playlist"
    "show"
    ]


let mapToArtistOutput (input: Input) (images: SpotifyAPI.Web.Image seq) (name: string) (source: 'a) : ArtistOutput =
    let images =
        images
        |> List.ofSeq
        |> List.map (fun image -> { Url = image.Url; Height = image.Height; Width = image.Width })
    {
        ShortName = input.ShortName
        HttpFriendlyShortName = input.HttpFriendlyShortName
        Type = input.Type
        Id = input.Id
        Icon = input.Icon
        CoverCenterX = input.CoverCenterX
        CoverCenterY = input.CoverCenterY
        AltCoverCenterX = input.AltCoverCenterX
        AltCoverCenterY = input.AltCoverCenterY
        CoverColorA = input.CoverColorA
        CoverColorB = input.CoverColorB
        Name = name
        Images = images
    }


let mapSpotifyImageToImage (image: SpotifyAPI.Web.Image) : Image =
    {
        Url = image.Url
        Height = image.Height
        Width = image.Width
    }


let mapSimpleAlbumToItemOutput (album: SimpleAlbum) : ItemOutput =
    {
        Id = album.Id
        Name = album.Name
        UrlToOpen = album.ExternalUrls.["spotify"]
        Images = album.Images 
                |> List.ofSeq 
                |> List.map mapSpotifyImageToImage
    }
    

let mapSimpleEpisodeToItemOutput (show: SimpleEpisode) : ItemOutput =
    {
        Id = show.Id
        Name = show.Name
        UrlToOpen = show.ExternalUrls.["spotify"]
        Images = show.Images
                |> List.ofSeq
                |> List.map mapSpotifyImageToImage
    }
    

let retrieveDataForArtist (client: SpotifyClient) (input: Input) : Task<Result<Output, string>> =
    taskResult {
        let! artistDetails = performRateLimitAwareRequest (fun () -> input.Id |> client.Artists.Get)
        let mappedArtist = artistDetails |> mapToArtistOutput input artistDetails.Images artistDetails.Name
        let! allAlbums = input.Id |> getAlbumsForArtist client
        let filteredAlbums =
            allAlbums
            |> List.map mapSimpleAlbumToItemOutput
            |> (filterAlbums (input.IgnoreIds |?| []) (input.IgnoreShowWithStrings |?| []))
        
        return {
            Albums = filteredAlbums
            Artist = mappedArtist
        }
    }
    
let retrieveDataForPlaylist (client: SpotifyClient) (input: Input) : Task<Result<Output, string>> =
    taskResult {
        try
            let! playlist = performRateLimitAwareRequest (fun () -> input.Id |> client.Playlists.Get)
            let! allTracks = performRateLimitAwareRequest (fun () -> client.PaginateAll(playlist.Tracks) |> Task.map List.ofSeq)
            let playlistTracks =
                    allTracks
                    |> List.map (fun element ->
                        match element.Track with
                        | :? FullTrack as track -> Some track
                        | :? FullEpisode -> failwith "Found an episode in a playlist. This might be valid but is not supported currently"
                        | other -> failwith $"Found an unknown type if IPlayableItem: ${other.GetType().FullName}")
                    |> List.choose id

            let albums = playlistTracks |> List.map (_.Album) |> List.distinctBy (_.Id)
            let mappedAlbums = albums |> List.map mapSimpleAlbumToItemOutput
            let filteredAlbums = mappedAlbums |> filterAlbums (input.IgnoreIds |?| []) (input.IgnoreShowWithStrings |?| [])
            
            return {
                Artist = playlist |> mapToArtistOutput input playlist.Images playlist.Name
                Albums = filteredAlbums
            }
        with ex ->
            return! Error $"Could not retrieve data for playlist {input.Id} because: {ex.Message}"
    } 
    
    
let retrieveDataForShow (client: SpotifyClient) (input: Input) : Task<Result<Output, string>> =
    task {
        try
            let! show = input.Id |> client.Shows.Get
            let! allEpisodes = client.PaginateAll(show.Episodes) |> Task.map List.ofSeq
            let filteredEpisodes =
                    allEpisodes
                    |> List.map (fun episode ->
                        {
                            Id = episode.Id
                            Name = episode.Name
                            UrlToOpen = episode.ExternalUrls["spotify"]
                            Images = episode.Images |> Seq.map mapSpotifyImageToImage |> List.ofSeq
                        })
                    |> filterAlbums (input.IgnoreIds |?| []) (input.IgnoreShowWithStrings |?| [])
            
            return Ok {
                Artist = show |> mapToArtistOutput input show.Images show.Name
                Albums = filteredEpisodes
            }
        with ex ->
            return Error $"Could not retrieve data for show {input.Id} because: {ex.Message}"
    }


let retrieveDataForInput (client: SpotifyClient) (input: Input) : Task<Result<Output, string>> =
    taskResult {
        printfn $"Retrieving data for {input.Type} {input.Id}"
        let! result =
            match input.Type.ToLowerInvariant() with
            | "artist" -> retrieveDataForArtist client input
            | "playlist" -> retrieveDataForPlaylist client input
            | "show" -> retrieveDataForShow client input
            | other -> failwith $"Input type {other} is unknown"
        do printfn $"Finished retrieving data for {input.Type} {input.Id}"
        return result
    }


let saveOutput (basedir: string) (output: Output) =
    let dir = Path.Combine(basedir, output.Artist.Id)
    do Directory.CreateDirectory(dir) |> ignore
    
    let artistFilename = Path.Combine(dir, "artist")
    let serializedArtist = JsonSerializer.Serialize(output.Artist, jsonSerializerOptions)
    do File.WriteAllText(artistFilename, serializedArtist)
    
    let itemsFilename = Path.Combine(dir, "albums")
    let serializedItems = JsonSerializer.Serialize(output.Albums, jsonSerializerOptions)
    do File.WriteAllText(itemsFilename, serializedItems)
    
        
let saveAllOutputs (basedir: string) (outputs: Output list) =
    let save = saveOutput basedir
    do printf "Removing previous output"
    if Directory.Exists(basedir) then Directory.Delete(basedir, true)
    do printfn " ... OK"
    
    do printf "Creating output directory"
    do Directory.CreateDirectory(basedir) |> ignore
    do printfn " ... OK"
    
    do printfn "Beginning saving all outputs"
    do outputs |> List.iter save
    do printfn "Finished writing all data"
        
        
let runTasksInSequence (tasks: Task<Result<Output, string>> list) =
    task {
        let results = System.Collections.Generic.List<Output>()
        let errors = System.Collections.Generic.List<string>()
        for task in tasks do
            match! task with
            | Ok output -> results.Add(output)
            | Error err -> errors.Add(err)
            
        if errors.Any() then return Error (String.Join(Environment.NewLine, errors))
        else return Ok (results |> List.ofSeq)
    }
    
    
let limitStringLength maxLength (input: string) : string =
    let ellipsis = " [...]"
    if input.Length <= maxLength then input
    else input.Substring(0, maxLength - ellipsis.Length) + ellipsis
    
    
let printStats (outputs: Output list) : unit =
    let table = Table()
    
    do TableExtensions.Title(table, Environment.NewLine + "Overview of downloaded data", null) |> ignore
    do table
           .AddColumn("#")
           .AddColumn("Id")
           .AddColumn("Name")
           .AddColumn("Count") |> ignore
    // we clip the titles to make the table's total width be 80 characters
    do outputs |> List.iteri (fun i output ->
        table.AddRow(
            Markup((i + 1) |> string),
            Markup(output.Artist.Id),
            Markup(Markup.Escape(output.Artist.Name |> limitStringLength 38)),
            Markup(output.Albums.Length |> string)
            ) |> ignore)
    do AnsiConsole.Write(table)
        

[<EntryPoint>]
let main args =
    let operation =
        taskResult {
            do printf "Trying to get cli arguments"
            let! config = args |> parseCommandLineArgs
            do printfn " ... OK"
            
            do printf "Trying to read input file"
            let! rawJson = config.InputFilePath |> readFileContent
            do printfn " ... OK"
            
            do printf "Trying to parse input file"
            let! source = rawJson |> parseInput
            do printfn " ... OK"
            
            do printf "Trying to authenticate"
            let! client = createSpotifyClient config.ClientId config.ClientSecret
            do printfn " ... OK"
            
            let! outputs = source |> List.map (retrieveDataForInput client) |> List.sequenceTaskResultM
            do printfn "Finished downloading all data"
            
            do outputs |> printStats
            
            do outputs |> saveAllOutputs config.OutputPath
                       
            return 0
        } |> TaskResult.defaultWith (fun error ->
            do printfn $" ... FAILED%s{Environment.NewLine}%s{error}"
            1)

    operation.GetAwaiter().GetResult()
    
