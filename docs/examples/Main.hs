main = interact (show . sum . map (read :: String -> Integer) . words)
