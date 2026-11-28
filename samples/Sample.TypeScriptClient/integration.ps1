# The TypeScript client end to end, against a real Sample.WebServer. The same client generated three
# ways: from the description the server's build writes from the model DLL, from the one the server
# writes as it starts, and from its introspection endpoint; the three are compared, as are the two
# files. Then the console sample generated from the build's file, type-checked and run; the browser
# sample built by Vite, type-checked, and driven headless in Edge or Chrome; and the client's own
# server tests, all against the same server.
#
# CI runs it after the suites, on the Windows job that has LocalDB. It runs the same way locally:
#   ./samples/Sample.TypeScriptClient/integration.ps1
param(
    [string] $Configuration = 'Debug',
    [int] $Port = 5260
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$sample = $PSScriptRoot
$web = Join-Path $root 'samples/Sample.TypeScriptWeb'
$client = Join-Path $root 'clients/typescript'
$server = "http://localhost:$Port"
$introspect = "$server/scry/introspect"
$work = Join-Path ([System.IO.Path]::GetTempPath()) "scry-typescript-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null

function Invoke-Checked([string] $what, [scriptblock] $command) {
    Write-Host "== $what"
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed ($LASTEXITCODE)."
    }
}

# A generated model without the line naming the input it was generated from, which is the one line
# three inputs of one description may differ in.
function Read-Model([string] $path) {
    (Get-Content $path | Where-Object { -not $_.StartsWith('// From: ') }) -join "`n"
}

$process = $null
try {
    # The build writes scry.description.json beside this script, from the model DLL, with no server.
    # GeneratePackageOnBuild=false, as CI passes to every build outside src.
    Invoke-Checked 'Building Sample.WebServer' {
        dotnet build (Join-Path $root 'samples/Sample.WebServer') -c $Configuration -p:GeneratePackageOnBuild=false
    }
    $built = Join-Path $sample 'scry.description.json'
    $builtForWeb = Join-Path $web 'scry.description.json'
    foreach ($path in $built, $builtForWeb) {
        if (-not (Test-Path $path)) {
            throw "The build wrote no $path."
        }
    }

    if ((Get-FileHash $built).Hash -ne (Get-FileHash $builtForWeb).Hash) {
        throw "The build wrote the two samples different descriptions."
    }

    Push-Location $client
    try {
        Invoke-Checked 'Installing the client' { npm ci --no-audit --no-fund }
    }
    finally {
        Pop-Location
    }

    Push-Location $sample
    try {
        Invoke-Checked 'Installing the sample' { npm ci --no-audit --no-fund }
    }
    finally {
        Pop-Location
    }

    Push-Location $web
    try {
        Invoke-Checked 'Installing the browser sample' { npm ci --no-audit --no-fund }
    }
    finally {
        Pop-Location
    }

    # Started as the browser suite starts it: the built DLL, from a folder of its own, in Development
    # for the explorer's introspection endpoint, on a database of its own so a developer's sample keeps
    # theirs. It writes its description as it starts, where SAMPLE_STARTUP_DESCRIPTION says.
    $startup = Join-Path $work 'startup.description.json'
    $info = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.ArgumentList.Add((Join-Path $root "samples/Sample.WebServer/bin/$Configuration/net11.0/Sample.WebServer.dll"))
    $info.WorkingDirectory = $work
    $info.UseShellExecute = $false
    $info.Environment['ASPNETCORE_URLS'] = $server
    $info.Environment['DOTNET_ENVIRONMENT'] = 'Development'
    $info.Environment['SAMPLE_DATABASE'] = 'TypeScript'
    $info.Environment['SAMPLE_STARTUP_DESCRIPTION'] = $startup
    $info.Environment['Logging__LogLevel__Microsoft.AspNetCore'] = 'Warning'
    $info.Environment['Logging__LogLevel__Microsoft.EntityFrameworkCore'] = 'Warning'
    Write-Host "== Starting Sample.WebServer at $server"
    $process = [System.Diagnostics.Process]::Start($info)

    $deadline = (Get-Date).AddMinutes(3)
    while ($true) {
        try {
            Invoke-WebRequest $introspect -UseBasicParsing | Out-Null
            break
        }
        catch {
            if ($process.HasExited) {
                throw "Sample.WebServer exited ($($process.ExitCode)) before it answered."
            }

            if ((Get-Date) -gt $deadline) {
                throw "Sample.WebServer did not answer at $introspect."
            }

            Start-Sleep -Milliseconds 500
        }
    }

    # What the build wrote and what the server wrote as it started are one description: both carry the
    # sample's page size, and neither the explorer's SQL offer.
    if ((Get-FileHash $built).Hash -ne (Get-FileHash $startup).Hash) {
        throw "The description the build wrote ($built) is not the one the server wrote as it started ($startup)."
    }

    Push-Location $sample
    try {
        $inputs = [ordered]@{
            build = $built
            startup = $startup
            url = $introspect
        }
        foreach ($name in $inputs.Keys) {
            Invoke-Checked "Generating the client from the $name description" {
                npx scry-codegen --input $inputs[$name] --output (Join-Path $work "$name.ts")
            }
        }

        $expected = Read-Model (Join-Path $work 'build.ts')
        foreach ($name in 'startup', 'url') {
            if ((Read-Model (Join-Path $work "$name.ts")) -ne $expected) {
                Compare-Object ($expected -split "`n") ((Read-Model (Join-Path $work "$name.ts")) -split "`n") | Select-Object -First 20 | Out-Host
                throw "The client generated from the $name description is not the one generated from the build's."
            }
        }

        Write-Host '== The three generated clients are the same'
        Invoke-Checked 'Generating the sample' { npm run generate }
        Invoke-Checked 'Type-checking the sample' { npm run typecheck }
        Invoke-Checked 'Running the sample' { node src/main.ts --server $server }
        Invoke-Checked "Opening the sample's photos" { node src/main.ts --server $server --photos }
    }
    finally {
        Pop-Location
    }

    # The plugin generates the browser sample's model as Vite's build starts; the test then serves the
    # page with Vite's dev server, forwarding /api to this server, and drives it in a real browser.
    $env:SCRY_SERVER = $server
    try {
        Push-Location $web
        try {
            Invoke-Checked 'Building the browser sample' { npm run build }
            Invoke-Checked 'Type-checking the browser sample' { npm run typecheck }
            Invoke-Checked 'Driving the browser sample in a browser' { npm test }
        }
        finally {
            Pop-Location
        }

        Push-Location $client
        try {
            Invoke-Checked "Running the client's server tests" { node --test test/server.test.ts }
        }
        finally {
            Pop-Location
        }
    }
    finally {
        Remove-Item Env:SCRY_SERVER
    }
}
finally {
    # The server alone, never its process tree, as the browser suite stops it: LocalDB's engine is a
    # child of whichever process started the instance, and other processes may be using it.
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit(5000) | Out-Null
        }

        $process.Dispose()
    }

    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
