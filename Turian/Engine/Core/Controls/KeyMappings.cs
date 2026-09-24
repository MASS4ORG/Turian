namespace Turian.Engine.Core;

struct KeyMappings
{
    public Key MoveLeft;
    public Key MoveRight;
    public Key MoveForward;
    public Key MoveBackward;
    public Key MoveUp;
    public Key MoveDown;
    public Key LookLeft;
    public Key LookRight;
    public Key LookUp;
    public Key LookDown;

    public KeyMappings()
    {
        MoveLeft = Key.A;
        MoveRight = Key.D;
        MoveForward = Key.W;
        MoveBackward = Key.S;
        MoveUp = Key.E;
        MoveDown = Key.Q;
        LookLeft = Key.Left;
        LookRight = Key.Right;
        LookUp = Key.Up;
        LookDown = Key.Down;
    }
};
