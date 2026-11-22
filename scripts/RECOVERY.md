# Manual recovery

Use this if PeakMode (or a test cycle) leaves the system tweaked and the
normal restore does not work. Run everything from an elevated PowerShell.

1. Restore from the snapshot (preferred, undoes exactly what was changed):

   ```powershell
   peakmode_test.exe restore        # or tray menu > Restore
   ```

   The snapshot is `C:\ProgramData\PeakMode\snapshot.json` and the log is
   `C:\ProgramData\PeakMode\peakmode.log` (Administrators only).

2. If the network adapter is still disabled:

   ```powershell
   Get-PnpDevice -Class Net | Where-Object Status -ne 'OK'
   Enable-PnpDevice -InstanceId '<InstanceId from above>' -Confirm:$false
   ```

   Or Device Manager > Network adapters > right-click the adapter > Enable.

3. If SysMain or Windows Search are stopped:

   ```powershell
   Start-Service SysMain, WSearch
   ```

4. If the power plan is wrong:

   ```powershell
   powercfg /list
   powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e   # Balanced
   ```

   A plan named "PeakMode (temporary)" can be removed with
   `powercfg /delete <GUID>` once another plan is active.

5. NVIDIA settings: NVIDIA Control Panel > Manage 3D settings > Global
   Settings > Restore.

6. Registry values (Nagle, NIC interrupt policy, adapter properties): the
   snapshot file lists every value with its original data, or
   `"existed": false` if PeakMode created it and it should be deleted.
