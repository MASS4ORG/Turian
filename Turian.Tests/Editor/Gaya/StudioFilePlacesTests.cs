namespace Turian.Tests;

/// <summary>The file dialog's drives come from the mount table, so a stuck mount cannot stop the dialog opening.</summary>
public sealed class StudioFilePlacesTests
{
    /// <summary>Real drives are listed, decoded; services, pseudo file systems and boot partitions are not.</summary>
    [Fact]
    public void OnlyBlockDevicesBecomePlaces()
    {
        string[] mounts =
        [
            "/dev/mapper/root / ext4 rw 0 0",
            "/dev/nvme0n1p8 /mass4 ext4 rw 0 0",
            "/dev/sdb1 /media/my\\040disk vfat rw 0 0",
            "/dev/nvme0n1p1 /boot/efi vfat rw 0 0",
            "/dev/loop3 /snap/core/1 squashfs ro 0 0",
            "portal /run/user/1000/doc fuse.portal rw 0 0",
            "tmpfs /tmp tmpfs rw 0 0",
        ];

        var places = StudioFilePlaces.Drives(mounts).ToArray();

        Assert.Equal(["/", "/mass4", "/media/my disk"], places.Select(static place => place.Path));
        Assert.Equal(["System", "mass4", "my disk"], places.Select(static place => place.Label));
    }

    /// <summary>A malformed line is ignored and a drive mounted twice is listed once.</summary>
    [Fact]
    public void MalformedAndRepeatedMountsAreSkipped()
    {
        string[] mounts = ["", "garbage", "/dev/sda1 /data ext4 rw 0 0", "/dev/sda1 /data ext4 rw 0 0"];

        Assert.Equal(["/data"], StudioFilePlaces.Drives(mounts).Select(static place => place.Path));
    }
}
