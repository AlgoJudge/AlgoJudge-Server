import image from '../../assets/algojudge2.png'
import classes from './Logo.module.css'
import { Image } from '@mantine/core';

function Logo(props: any) {
    return (
        <Image className={classes.ajlogo} src={image} h="1em" w="auto" {...props} />
    )
}

export default Logo;
