@{
    # Write-Host is deliberate here: these are console scripts whose whole job is to print
    # progress and colour while a release or a verification runs. Write-Information or
    # Write-Verbose would hide that output by default.
    ExcludeRules = @(
        'PSAvoidUsingWriteHost'
    )
}
