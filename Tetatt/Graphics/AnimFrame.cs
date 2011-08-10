﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetatt.Graphics
{
    public class AnimFrame
    {
	    public int tile;
	    public int delay;

        public AnimFrame(int tile, int delay = 1)
		{
            this.tile = tile;
            this.delay = delay;
        }
    }
}