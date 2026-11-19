namespace UotanStudio.Tests.DD;

internal static class MacOpticalDeviceFixtures
{
    internal const string RealListXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE  [
            <!ELEMENT deviceList (device)*>
            <!ELEMENT device (vendor, product, firmware, interconnect, support)>
            <!ATTLIST device index CDATA #IMPLIED>
            <!ELEMENT vendor EMPTY>
            <!ATTLIST vendor name CDATA #REQUIRED>
            <!ELEMENT product EMPTY>
            <!ATTLIST product name CDATA #REQUIRED>
            <!ELEMENT firmware EMPTY>
            <!ATTLIST firmware revision CDATA #REQUIRED>
            <!ELEMENT interconnect EMPTY>
            <!ATTLIST interconnect name CDATA #REQUIRED>
            <!ELEMENT support EMPTY>
            <!ATTLIST support level (appleShipping|appleSupported|vendorSupported|unSupported|notSupported) #REQUIRED>
        ]>

        <deviceList>
            <device name="ASUS SBW-06D2X-U" index="1">
                <vendor name="ASUS"/>
                <product name="SBW-06D2X-U"/>
                <firmware revision="1.10"/>
                <interconnect name="USB"/>
                <support level="unSupported"/>
            </device>
        </deviceList>
        """;

    internal const string NoMediaStatusXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE statusdoc [
            <!ELEMENT statusdoc (statusfordevice)*>
            <!ELEMENT statusfordevice (device, deviceStatus)>
            <!ELEMENT deviceStatus (writeSpeed, mediaInfo?, mediaIsPresent?, mediaIsInTransition?, mediaIsBusy?, trayState)>
        ]>

        <statusdoc>
            <statusfordevice>
                <device name="ASUS SBW-06D2X-U">
                    <vendor name="ASUS"/>
                    <product name="SBW-06D2X-U"/>
                    <firmware revision="1.10"/>
                    <interconnect name="USB"/>
                    <support level="unSupported"/>
                </device>
                <deviceStatus>
                    <writeSpeed maxSpeed="0" currentSpeed="0"/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;

    internal const string DvdStatusXml = """
        <statusdoc>
            <statusfordevice>
                <device name="HL-DT-ST BD-RE BH16NS40" index="1"/>
                <deviceStatus>
                    <writeSpeed maxSpeed="16" currentSpeed="16"/>
                    <mediaInfo>
                        <blank/>
                        <usedSpace msf="00:00:00"/>
                        <freeSpace msf="229:59:74"/>
                        <overwritableSpace msf="00:00:00"/>
                        <mediaClass value="DVD"/>
                        <mediaType value="DVD-R"/>
                        <trackCount value="1"/>
                        <sessionCount value="0"/>
                    </mediaInfo>
                    <mediaIsPresent/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;

    internal const string BluRayStatusXml = """
        <statusdoc>
            <statusfordevice>
                <device name="HL-DT-ST BD-RE BH16NS40" index="1"/>
                <deviceStatus>
                    <writeSpeed maxSpeed="12" currentSpeed="12"/>
                    <mediaInfo>
                        <blank/>
                        <usedSpace msf="00:00:00"/>
                        <freeSpace msf="1145:11:00"/>
                        <overwritableSpace msf="00:00:00"/>
                        <mediaClass value="Unknown"/>
                        <mediaType value="BD-R"/>
                        <trackCount value="1"/>
                        <sessionCount value="0"/>
                    </mediaInfo>
                    <mediaIsPresent/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;

    internal const string CdStatusXml = """
        <statusdoc>
            <statusfordevice>
                <device name="ASUS SBW-06D2X-U" index="1"/>
                <deviceStatus>
                    <writeSpeed maxSpeed="48" currentSpeed="48"/>
                    <mediaInfo>
                        <blank/>
                        <usedSpace msf="00:00:00"/>
                        <freeSpace msf="79:59:74"/>
                        <overwritableSpace msf="00:00:00"/>
                        <mediaClass value="CD"/>
                        <mediaType value="CD-R"/>
                        <trackCount value="1"/>
                        <sessionCount value="0"/>
                    </mediaInfo>
                    <mediaIsPresent/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;

    internal const string AppendableStatusXml = """
        <statusdoc>
            <statusfordevice>
                <device name="ASUS SBW-06D2X-U" index="1"/>
                <deviceStatus>
                    <writeSpeed maxSpeed="24" currentSpeed="24"/>
                    <mediaInfo>
                        <usedSpace msf="00:04:00"/>
                        <freeSpace msf="79:55:74"/>
                        <overwritableSpace msf="00:00:00"/>
                        <appendable/>
                        <mediaClass value="CD"/>
                        <mediaType value="CD-R"/>
                    </mediaInfo>
                    <mediaIsPresent/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;

    internal const string BusyStatusXml = """
        <statusdoc>
            <statusfordevice>
                <device name="ASUS SBW-06D2X-U" index="1"/>
                <deviceStatus>
                    <writeSpeed maxSpeed="24" currentSpeed="0"/>
                    <mediaInfo>
                        <blank/>
                        <mediaClass value="CD"/>
                        <mediaType value="CD-R"/>
                    </mediaInfo>
                    <mediaIsPresent/>
                    <mediaIsBusy/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;

    internal const string TransitionStatusXml = """
        <statusdoc>
            <statusfordevice>
                <device name="ASUS SBW-06D2X-U" index="1"/>
                <deviceStatus>
                    <writeSpeed maxSpeed="24" currentSpeed="0"/>
                    <mediaIsInTransition/>
                    <trayState value="closed"/>
                </deviceStatus>
            </statusfordevice>
        </statusdoc>
        """;
}