﻿$es_version = "1.6.0"
if ($env:ES_VERSION) {
    $es_version = $env:ES_VERSION
}

if (!(Test-Path -Path $env:JAVA_HOME)) {
    Write-Error "Please ensure the latest version of java is installed and the JAVA_HOME environmental variable has been set."
    Return
}

Push-Location $PSScriptRoot

if (!(Test-Path -Path "elasticsearch-$es_version") -And !(Test-Path -Path "elasticsearch.zip")) {
    wget "http://download.elasticsearch.org/elasticsearch/elasticsearch/elasticsearch-$es_version.zip" -OutFile "elasticsearch.zip"
}

if ((Test-Path -Path "elasticsearch.zip") -And !(Test-Path -Path "elasticsearch-$es_version")) {
    Add-Type -assembly "system.io.compression.filesystem"
    [io.compression.zipfile]::ExtractToDirectory("$PSScriptRoot\elasticsearch.zip", $PSScriptRoot)
    cp .\elasticsearch.yml .\elasticsearch-$es_version\config -Force
    rm elasticsearch.zip
}

Start-Process -NoNewWindow "$(Get-Location)\elasticsearch-$es_version\bin\elasticsearch.bat"

Pop-Location