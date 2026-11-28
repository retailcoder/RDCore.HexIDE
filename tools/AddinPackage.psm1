#Requires -Version 7
<#
.SYNOPSIS
    The add-in package's values as the repository holds them, each read from the one place it is kept, and the checks of a
    signed package. Pack-Addin.ps1 and Test-AddinPackage.ps1 import it, so neither types a value the other reads.
#>

$ErrorActionPreference = 'Stop'

# The add-in's id: RDCoreServer.Id, the constant the add-in registers its language server with, read from the built assembly.
function Get-PackAddinId([string] $assembly) {
    $stream = [IO.File]::OpenRead($assembly)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($typeHandle in $reader.TypeDefinitions) {
            $type = $reader.GetTypeDefinition($typeHandle)
            if ($reader.GetString($type.Namespace) -ne 'RDCore.HexIDE.Server' -or $reader.GetString($type.Name) -ne 'RDCoreServer') { continue }
            foreach ($fieldHandle in $type.GetFields()) {
                $field = $reader.GetFieldDefinition($fieldHandle)
                if ($reader.GetString($field.Name) -ne 'Id') { continue }
                $constant = $field.GetDefaultValue()
                if ($constant.IsNil) { break }
                $blob = $reader.GetBlobReader($reader.GetConstant($constant).Value)
                return $blob.ReadUTF16($blob.Length)
            }
        }
        throw "$assembly has no RDCore.HexIDE.Server.RDCoreServer.Id constant to take the add-in's id from."
    }
    finally {
        $pe.Dispose()
        $stream.Dispose()
    }
}

# The publisher's id: the publisher record's, which HexIDE issued. The packer takes it from the record too, never from an option.
function Get-PackPublisherId([string] $trust) {
    $record = Get-Content (Join-Path $trust 'publisher.json') -Raw | ConvertFrom-Json
    if (-not $record.publisherId) { throw "$(Join-Path $trust 'publisher.json') names no publisherId." }
    $record.publisherId
}

# A public key's SHA-256 fingerprint, in groups of four hex digits: the form TRUST.md and the README publish.
function Get-PackKeyFingerprint([string] $publicKey) {
    $hex = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($publicKey.Trim())))
    ($hex -split '(.{4})' | Where-Object { $_ }) -join ' '
}

# Whether $signatureFile (base64, IEEE P1363, as the packer writes it) signs the exact bytes of $file under $publicKey.
function Test-PackSignature([string] $publicKey, [string] $file, [string] $signatureFile) {
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $read = 0
        $key.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($publicKey.Trim()), [ref] $read)
        $signature = [Convert]::FromBase64String((Get-Content $signatureFile -Raw).Trim())
        $key.VerifyData([IO.File]::ReadAllBytes($file), $signature, [Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally { $key.Dispose() }
}

Export-ModuleMember -Function Get-PackAddinId, Get-PackPublisherId, Get-PackKeyFingerprint, Test-PackSignature
