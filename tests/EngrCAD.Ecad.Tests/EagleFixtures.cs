namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Transcribed (minimal but realistic) Eagle <c>.lbr</c> XML for the reader tests — one library
/// carrying an 0805 SMD resistor, an 8-pin IC available as both an SMD (SOIC8) and a through-hole
/// (DIL08) package, a deliberately-inconsistent device (a package missing a pad), a device with an
/// unmapped symbol pin, and a multi-gate device. Coordinates are in millimetres, as Eagle stores
/// them in the XML.
/// </summary>
internal static class EagleFixtures
{
    // A whole minimal library: symbols, packages and devicesets, with the <connect> map that
    // binds symbol pins to pads. Eagle pin rot=R0 points +x (into the body), so a left pin uses
    // R0 and a right pin R180 — the anchor is the connection point where a wire lands.
    internal const string Library = """
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE eagle SYSTEM "eagle.dtd">
<eagle version="7.7.0">
  <drawing>
    <settings>
      <setting alwaysvectorfont="no"/>
      <setting verticaltext="up"/>
    </settings>
    <grid distance="0.1" unitdist="inch" unit="inch" style="lines"/>
    <layers>
      <layer number="1" name="Top" color="4" fill="1" visible="yes" active="yes"/>
      <layer number="94" name="Symbols" color="4" fill="1" visible="yes" active="yes"/>
    </layers>
    <library>
      <description>A minimal test library.</description>
      <packages>
        <package name="R0805">
          <description>0805 SMD resistor.</description>
          <wire x1="-1.6" y1="0.7" x2="1.6" y2="0.7" width="0.1016" layer="21"/>
          <smd name="1" x="-0.9125" y="0" dx="1.025" dy="1.4" layer="1" roundness="25"/>
          <smd name="2" x="0.9125" y="0" dx="1.025" dy="1.4" layer="1" roundness="25"/>
          <text x="0" y="1.0" size="0.8" layer="25">&gt;NAME</text>
        </package>
        <package name="SOIC8">
          <description>SOIC-8.</description>
          <smd name="1" x="-2.475" y="-1.905" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="2" x="-2.475" y="-0.635" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="3" x="-2.475" y="0.635" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="4" x="-2.475" y="1.905" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="5" x="2.475" y="1.905" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="6" x="2.475" y="0.635" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="7" x="2.475" y="-0.635" dx="1.95" dy="0.6" layer="1" roundness="25"/>
          <smd name="8" x="2.475" y="-1.905" dx="1.95" dy="0.6" layer="1" roundness="25"/>
        </package>
        <package name="DIL08">
          <description>DIP-8 through-hole.</description>
          <pad name="1" x="-3.81" y="3.81" drill="0.8" shape="octagon"/>
          <pad name="2" x="-3.81" y="1.27" drill="0.8" diameter="1.5" shape="round"/>
          <pad name="3" x="-3.81" y="-1.27" drill="0.8"/>
          <pad name="4" x="-3.81" y="-3.81" drill="0.8" shape="square"/>
          <pad name="5" x="3.81" y="-3.81" drill="0.8" shape="long"/>
          <pad name="6" x="3.81" y="-1.27" drill="0.8"/>
          <pad name="7" x="3.81" y="1.27" drill="0.8"/>
          <pad name="8" x="3.81" y="3.81" drill="0.8"/>
          <hole x="0" y="0" drill="1.0"/>
        </package>
        <package name="SOIC8_BAD">
          <description>SOIC-8 with pad 8 missing and a stray pad 99.</description>
          <smd name="1" x="-2.475" y="-1.905" dx="1.95" dy="0.6" layer="1"/>
          <smd name="2" x="-2.475" y="-0.635" dx="1.95" dy="0.6" layer="1"/>
          <smd name="3" x="-2.475" y="0.635" dx="1.95" dy="0.6" layer="1"/>
          <smd name="4" x="-2.475" y="1.905" dx="1.95" dy="0.6" layer="1"/>
          <smd name="5" x="2.475" y="1.905" dx="1.95" dy="0.6" layer="1"/>
          <smd name="6" x="2.475" y="0.635" dx="1.95" dy="0.6" layer="1"/>
          <smd name="7" x="2.475" y="-0.635" dx="1.95" dy="0.6" layer="1"/>
          <smd name="99" x="4.0" y="0" dx="1.95" dy="0.6" layer="1"/>
        </package>
      </packages>
      <symbols>
        <symbol name="R">
          <rectangle x1="-1.016" y1="-2.54" x2="1.016" y2="2.54" layer="94"/>
          <pin name="1" x="0" y="3.81" visible="off" length="short" direction="pas" rot="R270"/>
          <pin name="2" x="0" y="-3.81" visible="off" length="short" direction="pas" rot="R90"/>
        </symbol>
        <symbol name="IC8">
          <rectangle x1="-2.54" y1="-5.08" x2="2.54" y2="5.08" layer="94"/>
          <text x="0" y="0" size="1.27" layer="94">IC</text>
          <pin name="IN1" x="-5.08" y="3.81" visible="pin" length="short" direction="in" rot="R0"/>
          <pin name="IN2" x="-5.08" y="1.27" visible="pin" length="short" direction="in" rot="R0"/>
          <pin name="IN3" x="-5.08" y="-1.27" visible="pin" length="short" direction="in" rot="R0"/>
          <pin name="GND" x="-5.08" y="-3.81" visible="pin" length="short" direction="pwr" rot="R0"/>
          <pin name="OUT4" x="5.08" y="-3.81" visible="pin" length="short" direction="out" rot="R180"/>
          <pin name="OUT3" x="5.08" y="-1.27" visible="pin" length="short" direction="out" rot="R180"/>
          <pin name="OUT2" x="5.08" y="1.27" visible="pin" length="short" direction="out" rot="R180"/>
          <pin name="VCC" x="5.08" y="3.81" visible="pin" length="short" direction="pwr" rot="R180"/>
        </symbol>
        <symbol name="Q3">
          <pin name="A" x="0" y="5.08" visible="off" length="short" direction="pas" rot="R270"/>
          <pin name="B" x="0" y="-5.08" visible="off" length="short" direction="pas" rot="R90"/>
        </symbol>
      </symbols>
      <devicesets>
        <deviceset name="R-EU_" prefix="R" uservalue="yes">
          <description>Resistor.</description>
          <gates>
            <gate name="G$1" symbol="R" x="0" y="0"/>
          </gates>
          <devices>
            <device name="R0805" package="R0805">
              <connects>
                <connect gate="G$1" pin="1" pad="1"/>
                <connect gate="G$1" pin="2" pad="2"/>
              </connects>
              <technologies>
                <technology name=""/>
              </technologies>
            </device>
          </devices>
        </deviceset>
        <deviceset name="IC-" prefix="U">
          <description>An 8-pin IC.</description>
          <gates>
            <gate name="G$1" symbol="IC8" x="0" y="0"/>
          </gates>
          <devices>
            <device name="SOIC8" package="SOIC8">
              <connects>
                <connect gate="G$1" pin="IN1" pad="1"/>
                <connect gate="G$1" pin="IN2" pad="2"/>
                <connect gate="G$1" pin="IN3" pad="3"/>
                <connect gate="G$1" pin="GND" pad="4"/>
                <connect gate="G$1" pin="OUT4" pad="5"/>
                <connect gate="G$1" pin="OUT3" pad="6"/>
                <connect gate="G$1" pin="OUT2" pad="7"/>
                <connect gate="G$1" pin="VCC" pad="8"/>
              </connects>
            </device>
            <device name="DIL08" package="DIL08">
              <connects>
                <connect gate="G$1" pin="IN1" pad="1"/>
                <connect gate="G$1" pin="IN2" pad="2"/>
                <connect gate="G$1" pin="IN3" pad="3"/>
                <connect gate="G$1" pin="GND" pad="4"/>
                <connect gate="G$1" pin="OUT4" pad="5"/>
                <connect gate="G$1" pin="OUT3" pad="6"/>
                <connect gate="G$1" pin="OUT2" pad="7"/>
                <connect gate="G$1" pin="VCC" pad="8"/>
              </connects>
            </device>
          </devices>
        </deviceset>
        <deviceset name="BAD-" prefix="U">
          <description>An IC whose package disagrees with the symbol.</description>
          <gates>
            <gate name="G$1" symbol="IC8" x="0" y="0"/>
          </gates>
          <devices>
            <device name="SOIC8" package="SOIC8_BAD">
              <connects>
                <connect gate="G$1" pin="IN1" pad="1"/>
                <connect gate="G$1" pin="IN2" pad="2"/>
                <connect gate="G$1" pin="IN3" pad="3"/>
                <connect gate="G$1" pin="GND" pad="4"/>
                <connect gate="G$1" pin="OUT4" pad="5"/>
                <connect gate="G$1" pin="OUT3" pad="6"/>
                <connect gate="G$1" pin="OUT2" pad="7"/>
                <connect gate="G$1" pin="VCC" pad="8"/>
              </connects>
            </device>
          </devices>
        </deviceset>
        <deviceset name="UNMAPPED-" prefix="Q">
          <description>A device whose symbol pin B has no connect.</description>
          <gates>
            <gate name="G$1" symbol="Q3" x="0" y="0"/>
          </gates>
          <devices>
            <device name="X" package="R0805">
              <connects>
                <connect gate="G$1" pin="A" pad="1"/>
              </connects>
            </device>
          </devices>
        </deviceset>
        <deviceset name="DUAL-" prefix="U">
          <description>A two-gate (gate array) device.</description>
          <gates>
            <gate name="G$1" symbol="R" x="-5.08" y="0"/>
            <gate name="G$2" symbol="R" x="5.08" y="0"/>
          </gates>
          <devices>
            <device name="X" package="R0805">
              <connects>
                <connect gate="G$1" pin="1" pad="1"/>
                <connect gate="G$2" pin="1" pad="2"/>
              </connects>
            </device>
          </devices>
        </deviceset>
      </devicesets>
    </library>
  </drawing>
</eagle>
""";

    // A tiny library with one curved wire in the symbol, to exercise the arc path.
    internal const string ArcLibrary = """
<?xml version="1.0" encoding="utf-8"?>
<eagle version="7.7.0">
  <drawing>
    <library>
      <packages>
        <package name="P1"><smd name="1" x="0" y="0" dx="1" dy="1" layer="1"/></package>
      </packages>
      <symbols>
        <symbol name="A">
          <wire x1="-1" y1="0" x2="1" y2="0" width="0.2" curve="90"/>
          <pin name="1" x="0" y="3" visible="off" length="short" direction="pas" rot="R270"/>
        </symbol>
      </symbols>
      <devicesets>
        <deviceset name="ARC" prefix="U">
          <gates><gate name="G$1" symbol="A" x="0" y="0"/></gates>
          <devices>
            <device name="D" package="P1">
              <connects><connect gate="G$1" pin="1" pad="1"/></connects>
            </device>
          </devices>
        </deviceset>
      </devicesets>
    </library>
  </drawing>
</eagle>
""";

    // A MANAGED (Eagle 9 / Fusion) library, transcribed from the newer XML's shape: urn
    // attributes on the library and on every element, a <packages3d> block declaring 3D packages
    // by URN, and a device binding one through <package3dinstances>. Coordinates and the
    // connect map are the classic library's, so the only thing under test is the new vocabulary.
    //
    // Devices:
    //   R-EU_R0805  binds RESC2012X70N (a "model" 3D package, listing package R0805)
    //   R-EU_R0805B binds RESC2012X70N-BOX ("box"), whose packageinstance names a package the
    //               library does not contain (a diagnostic, not a refusal)
    //   R-EU_R0805X binds an urn <packages3d> never declares (a diagnostic)
    //   R-EU_R0805T binds BOTH real 3D packages (only the first can be attached)
    internal const string ManagedLibrary = """
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE eagle SYSTEM "eagle.dtd">
<eagle version="9.6.2">
  <drawing>
    <library urn="urn:adsk.eagle:library:325">
      <description>A minimal managed test library.</description>
      <packages>
        <package name="R0805" urn="urn:adsk.eagle:footprint:23122/1" library_version="1">
          <smd name="1" x="-0.9125" y="0" dx="1.025" dy="1.4" layer="1" roundness="25"/>
          <smd name="2" x="0.9125" y="0" dx="1.025" dy="1.4" layer="1" roundness="25"/>
        </package>
      </packages>
      <packages3d>
        <package3d name="RESC2012X70N" urn="urn:adsk.eagle:package:23123/2" type="model" library_version="1">
          <description>Chip, 2.00 X 1.25 X 0.70 mm body</description>
          <packageinstances>
            <packageinstance name="R0805"/>
          </packageinstances>
        </package3d>
        <package3d name="RESC2012X70N-BOX" urn="urn:adsk.eagle:package:23124/1" type="box" library_version="1">
          <packageinstances>
            <packageinstance name="R0805_NOT_HERE"/>
          </packageinstances>
        </package3d>
        <package3d name="NAMELESS" type="model">
          <packageinstances>
            <packageinstance name="R0805"/>
          </packageinstances>
        </package3d>
      </packages3d>
      <symbols>
        <symbol name="R" urn="urn:adsk.eagle:symbol:16966/1" library_version="1">
          <rectangle x1="-1.016" y1="-2.54" x2="1.016" y2="2.54" layer="94"/>
          <pin name="1" x="0" y="3.81" visible="off" length="short" direction="pas" rot="R270"/>
          <pin name="2" x="0" y="-3.81" visible="off" length="short" direction="pas" rot="R90"/>
        </symbol>
      </symbols>
      <devicesets>
        <deviceset name="R-EU_" prefix="R" uservalue="yes" urn="urn:adsk.eagle:component:23134/1" library_version="1">
          <gates>
            <gate name="G$1" symbol="R" x="0" y="0"/>
          </gates>
          <devices>
            <device name="R0805" package="R0805">
              <connects>
                <connect gate="G$1" pin="1" pad="1"/>
                <connect gate="G$1" pin="2" pad="2"/>
              </connects>
              <package3dinstances>
                <package3dinstance package3d_urn="urn:adsk.eagle:package:23123/2"/>
              </package3dinstances>
              <technologies><technology name=""/></technologies>
            </device>
            <device name="R0805B" package="R0805">
              <connects>
                <connect gate="G$1" pin="1" pad="1"/>
                <connect gate="G$1" pin="2" pad="2"/>
              </connects>
              <package3dinstances>
                <package3dinstance package3d_urn="urn:adsk.eagle:package:23124/1"/>
              </package3dinstances>
            </device>
            <device name="R0805X" package="R0805">
              <connects>
                <connect gate="G$1" pin="1" pad="1"/>
                <connect gate="G$1" pin="2" pad="2"/>
              </connects>
              <package3dinstances>
                <package3dinstance package3d_urn="urn:adsk.eagle:package:99999/9"/>
              </package3dinstances>
            </device>
            <device name="R0805T" package="R0805">
              <connects>
                <connect gate="G$1" pin="1" pad="1"/>
                <connect gate="G$1" pin="2" pad="2"/>
              </connects>
              <package3dinstances>
                <package3dinstance package3d_urn="urn:adsk.eagle:package:23123/2"/>
                <package3dinstance package3d_urn="urn:adsk.eagle:package:23124/1"/>
              </package3dinstances>
            </device>
          </devices>
        </deviceset>
      </devicesets>
    </library>
  </drawing>
</eagle>
""";

    // A board (.brd), not a library — refused by name at the root.
    internal const string BoardFile = """
<?xml version="1.0" encoding="utf-8"?>
<eagle version="7.7.0">
  <drawing>
    <board>
      <plain/>
    </board>
  </drawing>
</eagle>
""";
}
