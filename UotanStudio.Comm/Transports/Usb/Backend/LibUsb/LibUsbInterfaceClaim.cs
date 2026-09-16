namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>Claims an interface before endpoint access, with bounded kernel-driver recovery.</summary>
internal static class LibUsbInterfaceClaim
{
    internal static bool TryClaim(Func<bool> claim, Action? detach, out Exception? error)
    {
        error = null;
        for (var attempt = 0; attempt < (detach is null ? 1 : 4); attempt++)
        {
            if (attempt > 0)
            {
                try { detach!(); }
                catch (Exception) { }
            }

            try
            {
                if (claim()) return true;
                error = new IOException("libusb refused the USB interface claim.");
            }
            catch (Exception ex) { error = ex; }

            if (detach is not null && attempt < 3) Thread.Sleep(50);
        }
        return false;
    }
}